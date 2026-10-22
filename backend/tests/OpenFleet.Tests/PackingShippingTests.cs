using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Contracts;
using static OpenFleet.Tests.TestDatabase;

namespace OpenFleet.Tests;

/// <summary>
/// P4：包裝逐支進行，同批次的框全部包完（或報廢）才併成一箱送去出貨站；貨車定時來把箱子載走。
/// 測試用的時間：包裝與檢驗每支 2 秒；貨車間隔是示範值 1 小時。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PackingShippingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const int Inspection = 7, Packing = 8;

    private TestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(postgres);
        await using var db = _database.NewContext();
        await db.ProcessSteps.Where(s => s.Kind == ProcessStepKind.Piece).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecondsPerPiece, 2d));
        await db.ProductionSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.PieceHandlingSeconds, 0d));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Bins_of_a_batch_are_packed_one_by_one_then_boxed_together_and_shipped_by_the_truck()
    {
        var batch = NewBatch();
        var first = await ArriveAsync("packing", Packing, 3, batch, 1);
        var second = await ArriveAsync("packing", Packing, 2, batch, 2);
        await AdvanceAsync(0);

        // 逐支包裝，每支 2 秒，沒有機台取放的時間。
        await AdvanceAsync(4);
        var packing = await StationAsync("packing");
        Assert.Equal((true, 2, 3), (packing.IsPacking, packing.Machining!.PiecesCompleted, packing.Machining.Quantity));

        // 第一框包完：留在包裝站等第二框，還沒有箱子、也沒有搬運。接著開始第二框。
        await AdvanceAsync(2);
        Assert.Equal((3, LotStatus.Packed, "packing"), await StateAsync(first.Id));
        Assert.Equal(3, (await StationAsync("packing")).OutboundQuantity);
        await using (var scope = _database.NewScope())
        {
            Assert.False(await scope.Db.Lots.AnyAsync(l => l.IsBox));
            Assert.False(await scope.Db.TransportTasks.AnyAsync());
        }

        // 第二框也包完：整批併成一箱，兩個料框空了；箱子等待搬運到出貨站。
        await AdvanceAsync(4);
        Assert.Equal((3, LotStatus.Boxed, "packing"), await StateAsync(first.Id));
        Assert.Equal((2, LotStatus.Boxed, "packing"), await StateAsync(second.Id));
        MaterialLot box;
        await using (var scope = _database.NewScope())
        {
            box = await scope.Db.Lots.SingleAsync(l => l.IsBox);
            Assert.Equal(($"{batch}-U-BOX", batch, 5, LotStatus.AwaitingTransport, "packing"),
                (box.LotNumber, box.BatchNumber, box.Quantity, box.Status, box.StationCode));
            var task = await scope.Db.TransportTasks.SingleAsync();
            Assert.Equal((box.Id, "packing", "shipping", 5), (task.LotId!.Value, task.FromStationCode, task.ToStationCode, task.Quantity));
        }

        // 送到出貨站：等貨車。
        await DeliverAsync();
        Assert.Equal((5, LotStatus.Staged, "shipping"), await StateAsync(box.Id));
        var summary = await ShippingAsync();
        Assert.Equal((1, 5, 0, 0), (summary.WaitingBoxes, summary.WaitingPieces, summary.ShippedBoxes, summary.ShippedPieces));

        // 貨車還沒到（每小時一班）：箱子還在。
        await AdvanceAsync(3000);
        Assert.Equal(LotStatus.Staged, (await StateAsync(box.Id)).Status);
        var truck = (await StationAsync("shipping")).Truck!;
        Assert.InRange(truck.RemainingSeconds, 1, 600);

        // 貨車到了：箱子載走，留下出貨紀錄與位置異動，開始倒數下一班。
        await AdvanceAsync(600);
        Assert.Equal((5, LotStatus.Shipped, "shipping"), await StateAsync(box.Id));
        summary = await ShippingAsync();
        Assert.Equal((0, 0, 1, 5, 3600d), (summary.WaitingBoxes, summary.WaitingPieces, summary.ShippedBoxes, summary.ShippedPieces, summary.TruckRemainingSeconds));
        Assert.Equal((batch, $"{batch}-U-BOX", 5), (summary.Shipments[0].BatchNumber, summary.Shipments[0].LotNumber, summary.Shipments[0].Quantity));
        await using (var scope = _database.NewScope())
        {
            var shipped = await scope.Db.InventoryMovements.SingleAsync(m => m.Reason == "Shipped");
            Assert.Equal((box.Id, 5, "station:shipping", "shipped"), (shipped.LotId, shipped.Quantity, shipped.From, shipped.To));
        }

        Assert.Equal(0, (await StationAsync("shipping")).InboundQuantity);
    }

    [Fact]
    public async Task Batch_waits_for_the_make_up_of_scrapped_pieces_before_it_is_boxed()
    {
        // 同一個批次：一框已經到包裝站，另一框還在品管，而且兩支都會被判尺寸不良。
        var batch = NewBatch();
        var packed = await ArriveAsync("packing", Packing, 1, batch, 1);
        var late = await ArriveAsync("qc", Inspection, 2, batch, 2);
        _database.Defects.Enqueue(DefectReason.Dimension, DefectReason.Dimension);
        await AdvanceAsync(0);

        // 包裝這一框 2 秒就包完；品管那一框還沒檢完，所以先不裝箱。
        await AdvanceAsync(2);
        Assert.Equal(LotStatus.Packed, (await StateAsync(packed.Id)).Status);
        await using (var scope = _database.NewScope())
        {
            Assert.False(await scope.Db.Lots.AnyAsync(l => l.IsBox));
        }

        // 品管那一框整框報廢：報廢的 2 支自動補做，批次要等補做的料包裝完才裝箱。
        await AdvanceAsync(2);
        Assert.Equal(LotStatus.Scrapped, (await StateAsync(late.Id)).Status);
        Assert.Equal(LotStatus.Packed, (await StateAsync(packed.Id)).Status);
        MaterialLot makeup;
        await using (var scope = _database.NewScope())
        {
            Assert.False(await scope.Db.Lots.AnyAsync(l => l.IsBox));
            makeup = await scope.Db.Lots.SingleAsync(l => l.IsMakeup);
            Assert.Equal((batch, 2, "raw-store"), (makeup.BatchNumber, makeup.Quantity, makeup.StationCode));
        }

        // 補做的料走完製程（這裡省略，直接放到包裝站）、包裝完：這一種零件才裝箱，數量回到原本的 3 支。
        await using (var scope = _database.NewScope())
        {
            await scope.Db.TransportTasks.Where(t => t.LotId == makeup.Id).ExecuteDeleteAsync();
            var lot = await scope.Db.Lots.SingleAsync(l => l.Id == makeup.Id);
            scope.Db.Entry(lot).Property(l => l.CurrentStepSequence).CurrentValue = Packing;
            lot.PlaceAt("packing", _database.Time.GetUtcNow());
            scope.Production.OnLotArrived(lot, _database.Time.GetUtcNow());
            await scope.Db.SaveChangesAsync();
        }

        await AdvanceAsync(0);
        await AdvanceAsync(4);
        await using (var scope = _database.NewScope())
        {
            var box = await scope.Db.Lots.SingleAsync(l => l.IsBox);
            Assert.Equal((3, LotStatus.AwaitingTransport, "packing"), (box.Quantity, box.Status, box.StationCode));
        }
    }

    [Fact]
    public async Task Each_part_gets_its_own_box_and_the_truck_only_takes_a_batch_once_every_part_has_arrived()
    {
        // 同一個批次：上沖已經到包裝站，下沖還在品管。
        var batch = NewBatch();
        await ArriveAsync("packing", Packing, 2, batch, 1, ProductParts.UpperPunch);
        await ArriveAsync("qc", Inspection, 4, batch, 1, ProductParts.LowerPunch);
        await AdvanceAsync(0);

        // 上沖包完：不用等下沖，這一種零件自己裝一箱送去出貨站。
        await AdvanceAsync(4);
        MaterialLot upperBox;
        await using (var scope = _database.NewScope())
        {
            upperBox = await scope.Db.Lots.SingleAsync(l => l.IsBox);
            Assert.Equal(($"{batch}-U-BOX", ProductParts.UpperPunch, 2), (upperBox.LotNumber, upperBox.PartCode, upperBox.Quantity));
        }

        await DeliverAsync("shipping");
        var summary = await ShippingAsync();
        Assert.Equal((1, 0), (summary.WaitingBoxes, summary.ReadyBoxes));

        // 貨車來了：下沖還沒到，上沖的箱子不載，留到下一班。
        await AdvanceAsync(3600);
        Assert.Equal(LotStatus.Staged, (await StateAsync(upperBox.Id)).Status);
        Assert.Equal(0, (await ShippingAsync()).ShippedBoxes);

        // 下沖檢完、包完、裝箱、送到出貨站：整批到齊。
        await DeliverAsync("packing");
        await AdvanceAsync(0);
        await AdvanceAsync(8);
        await DeliverAsync("shipping");
        summary = await ShippingAsync();
        Assert.Equal((2, 2), (summary.WaitingBoxes, summary.ReadyBoxes));

        // 下一班貨車把兩箱一起載走。
        await AdvanceAsync(3600);
        summary = await ShippingAsync();
        Assert.Equal((0, 2, 6), (summary.WaitingBoxes, summary.ShippedBoxes, summary.ShippedPieces));
        Assert.Equal(new[] { ProductParts.LowerPunch, ProductParts.UpperPunch }, summary.Shipments.Select(s => s.PartCode).Order());
    }

    [Fact]
    public async Task Packed_bin_waits_for_a_bin_that_is_still_being_inspected()
    {
        var batch = NewBatch();
        var packed = await ArriveAsync("packing", Packing, 1, batch, 1);
        var late = await ArriveAsync("qc", Inspection, 3, batch, 2);
        await AdvanceAsync(0);

        // 包裝 2 秒就包完，但品管那一框還要 6 秒：先等。
        await AdvanceAsync(2);
        Assert.Equal(LotStatus.Packed, (await StateAsync(packed.Id)).Status);
        Assert.Equal(1, (await ShippingAsync()).PackedPieces);

        // 品管檢完、合格，送到包裝站包完之後才裝箱：一箱 4 支。
        await AdvanceAsync(4);
        await DeliverAsync();
        await AdvanceAsync(0);
        await AdvanceAsync(6);
        Assert.Equal(LotStatus.Boxed, (await StateAsync(late.Id)).Status);
        await using var scope = _database.NewScope();
        Assert.Equal(4, (await scope.Db.Lots.SingleAsync(l => l.IsBox)).Quantity);
    }

    [Fact]
    public async Task Test_mode_speeds_up_the_truck_and_changing_the_interval_restarts_the_countdown()
    {
        var batch = NewBatch();
        await ArriveAsync("packing", Packing, 1, batch, 1);
        await AdvanceAsync(0);
        await AdvanceAsync(2);
        await DeliverAsync();

        // 間隔改成 10 分鐘：從現在重新倒數。測試模式下貨車 60 倍，所以實際只要 10 秒。
        await using (var scope = _database.NewScope())
        {
            var settings = await scope.Production.GetSettingsAsync(default);
            settings.TruckIntervalSeconds = 600;
            settings.TestMode = true;
            settings.TestTruckSpeed = 60;
            await scope.Production.UpdateSettingsAsync(settings, default);
        }

        await AdvanceAsync(0);
        await AdvanceAsync(9);
        Assert.Equal(0, (await ShippingAsync()).ShippedBoxes);
        Assert.Equal(60, (await ShippingAsync()).TruckRemainingSeconds, 3);

        await AdvanceAsync(1);
        Assert.Equal((1, 1), ((await ShippingAsync()).ShippedBoxes, (await ShippingAsync()).ShippedPieces));

        // 站上沒有箱子時貨車空車離開，不產生出貨紀錄。
        await AdvanceAsync(10);
        Assert.Equal(1, (await ShippingAsync()).ShippedBoxes);

        // 間隔不能短於 1 分鐘。
        await using (var scope = _database.NewScope())
        {
            var settings = await scope.Production.GetSettingsAsync(default);
            settings.TruckIntervalSeconds = 30;
            await Assert.ThrowsAsync<OpenFleet.Api.Production.RequestValidationException>(() => scope.Production.UpdateSettingsAsync(settings, default));
        }
    }

    private static string NewBatch() => $"B-{Guid.NewGuid():N}"[..10];

    /// <summary>把批次裡的一框直接放到某站排隊（省略前面的工序）。</summary>
    private async Task<MaterialLot> ArriveAsync(
        string station, int step, int quantity, string batch, int binIndex, string partCode = ProductParts.UpperPunch)
    {
        await using var scope = _database.NewScope();
        var now = _database.Time.GetUtcNow();
        var lot = MaterialLot.Receive(partCode, quantity, station, step, batch, binIndex, 2, 30, now);
        lot.PlaceAt(station, now);
        scope.Db.Lots.Add(lot);
        scope.Production.OnLotArrived(lot, now);
        await scope.Db.SaveChangesAsync();
        _database.Time.Advance(TimeSpan.FromMilliseconds(100));
        return lot;
    }

    /// <summary>時間前進 <paramref name="seconds"/> 秒並把加工推進到現在。</summary>
    private async Task AdvanceAsync(double seconds)
    {
        _database.Time.Advance(TimeSpan.FromSeconds(seconds));
        await using var scope = _database.NewScope();
        await scope.Production.AdvanceOperationsAsync(default);
    }

    /// <summary>派出等待中的一趟搬運並送達；指定 <paramref name="to"/> 時確認送到的是那一站。</summary>
    private async Task DeliverAsync(string? to = null)
    {
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online("agv-a")], default));
            if (to is not null)
            {
                Assert.Equal(to, (await scope.Db.TransportTasks.SingleAsync(t => t.Status == TransportTaskStatus.Assigned)).ToStationCode);
            }
        }

        await _database.CompleteDeliveryAsync("agv-a");
    }

    private async Task<(int Quantity, LotStatus Status, string? Station)> StateAsync(Guid lotId)
    {
        await using var scope = _database.NewScope();
        var lot = await scope.Db.Lots.SingleAsync(l => l.Id == lotId);
        return (lot.Quantity, lot.Status, lot.StationCode);
    }

    private async Task<StationStatusDto> StationAsync(string code)
    {
        await using var scope = _database.NewScope();
        return (await scope.Production.GetStationStatusAsync(default)).Single(s => s.Code == code);
    }

    private async Task<ShippingSummaryDto> ShippingAsync()
    {
        await using var scope = _database.NewScope();
        return await scope.Production.GetShippingSummaryAsync(default);
    }
}
