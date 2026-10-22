using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFleet.Api.Data;
using OpenFleet.Api.Inventory;
using OpenFleet.Contracts;

namespace OpenFleet.Tests;

/// <summary>
/// P5 第 3 步：切料站。建立批次時先用現成的備料，不夠的從圓棒切；圓棒不夠的框先等料，到貨後接上；
/// 切完領用圓棒、多出來的備料入庫；補做的料也要有實際的備料或圓棒。
/// 示範庫存：圓棒 40 根、P30-RD12 三種備料各 40 支；圓棒 1500 mm、切口 3 mm，上沖 110 mm 一根切 13 支。
/// 測試用的時間：逐件工序每支 1 秒、沒有機台取放時間。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CuttingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Bar = "BAR-D40";
    private TestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(postgres, plentyOfBlanks: false);
        await using var db = _database.NewContext();
        await db.ProcessSteps.Where(s => s.Kind == ProcessStepKind.Piece).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecondsPerPiece, 1d));
        await db.ProductionSettings.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.PieceHandlingSeconds, 0d)
            .SetProperty(x => x.ToolDeliveryEnabled, false));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Batch_uses_stock_blanks_first_and_cuts_the_rest_from_whole_bars()
    {
        List<MaterialLot> bins;
        await using (var scope = _database.NewScope())
        {
            bins = await scope.Production.CreateBatchAsync(120, null, [ProductParts.UpperPunch], "P30-RD12", default);
        }

        // 現成的 40 支裝成 30、10 兩框直接送粗加工；要切的 80 支裝成 30、30、20 三框先送切料站。
        Assert.Equal([30, 10, 30, 30, 20], bins.Select(b => b.Quantity));
        Assert.All(bins, b => Assert.Equal(("P30-RD12", LotStatus.AwaitingTransport, 5), (b.SpecCode, b.Status, b.BinCount)));
        Assert.Equal([1, 1, 0, 0, 0], bins.Select(b => b.CurrentStepSequence!.Value));

        await using var db = _database.NewContext();
        var destinations = await db.TransportTasks.AsNoTracking().Select(t => t.ToStationCode).ToListAsync();
        Assert.Equal(["cutting", "cutting", "cutting", "rough", "rough"], destinations.Order());

        // 80 支、一根 13 支 → 7 根（91 支），多 11 支算在最後一框；圓棒依累計支數分到各框：3、2、2 根。
        var jobs = await db.CuttingJobs.AsNoTracking().OrderBy(j => j.LotNumber).ToListAsync();
        Assert.Equal([(3, 30, 0), (2, 30, 0), (2, 31, 11)], jobs.Select(j => (j.Bars, j.PiecesToCut, j.ExtraBlanks)));
        Assert.All(jobs, j => Assert.True(j.IsReady));

        var bar = await BalanceAsync(Bar);
        var blank = await BalanceAsync("P30-RD12-U-BLK");
        Assert.Equal((40, 7), (bar.OnHand, bar.Reserved));
        Assert.Equal((0, 0), (blank.OnHand, blank.Reserved));
    }

    [Fact]
    public async Task Cutting_a_bin_uses_its_bars_and_puts_the_extra_blanks_into_stock()
    {
        List<MaterialLot> bins;
        await using (var scope = _database.NewScope())
        {
            bins = await scope.Production.CreateBatchAsync(20, null, [ProductParts.UpperPunch], "P30-HX10", default);
        }

        // 20 支、一根 13 支：2 根、切 26 支、多 6 支。
        var lot = Assert.Single(bins);
        await DeliverAsync(lot.LotNumber, "cutting");
        await AdvanceAsync(0);
        await using (var db = _database.NewContext())
        {
            var operation = await db.Operations.AsNoTracking().SingleAsync(o => o.LotId == lot.Id);
            Assert.Equal((OperationStatus.Running, 26), (operation.Status, operation.Quantity));
        }

        await AdvanceAsync(26);

        await using (var db = _database.NewContext())
        {
            var cut = await db.Lots.AsNoTracking().SingleAsync(l => l.Id == lot.Id);
            Assert.Equal((LotStatus.AwaitingTransport, 1, 20), (cut.Status, cut.CurrentStepSequence!.Value, cut.Quantity));
            Assert.True(await db.TransportTasks.AnyAsync(t => t.LotId == lot.Id && t.FromStationCode == "cutting" && t.ToStationCode == "rough"));
            Assert.NotNull((await db.CuttingJobs.AsNoTracking().SingleAsync(j => j.LotId == lot.Id)).CompletedAt);
        }

        var bar = await BalanceAsync(Bar);
        Assert.Equal((38, 0), (bar.OnHand, bar.Reserved));
        Assert.Equal(6, (await BalanceAsync("P30-HX10-U-BLK")).OnHand);

        await using var read = _database.NewContext();
        var inventory = new InventoryService(read, _database.Time, NullLogger<InventoryService>.Instance);
        Assert.Equal((StockMovementReason.Consumption, -2, lot.LotNumber), Single(await inventory.GetMovementsAsync(Bar, 1, default)));
        Assert.Equal((StockMovementReason.Production, 6, lot.LotNumber), Single(await inventory.GetMovementsAsync("P30-HX10-U-BLK", 1, default)));

        static (StockMovementReason, int, string?) Single(List<StockMovementDto> movements) =>
            (movements[0].Reason, movements[0].Quantity, movements[0].Reference);
    }

    [Fact]
    public async Task Bins_wait_for_bars_in_order_and_start_as_soon_as_purchased_bars_arrive()
    {
        await AdjustBarsAsync(-36);

        List<MaterialLot> bins;
        await using (var scope = _database.NewScope())
        {
            bins = await scope.Production.CreateBatchAsync(120, null, [ProductParts.UpperPunch], "P30-HX10", default);
        }

        // 120 支、一根 13 支：4 框各要 3、2、2、3 根。只有 4 根：第 1 框備齊，第 2 框先保留到 1 根，後面兩框在原料倉等料。
        Assert.Equal(
            [LotStatus.AwaitingTransport, LotStatus.AwaitingMaterial, LotStatus.AwaitingMaterial, LotStatus.AwaitingMaterial],
            bins.Select(b => b.Status));
        Assert.Equal([(3, 3), (2, 1), (2, 0), (3, 0)], await JobsAsync());
        await using (var db = _database.NewContext())
        {
            Assert.Equal(1, await db.TransportTasks.CountAsync());
        }

        // 採購 5 根、收貨：下一輪依建立順序接上，第 2、3 框備齊，第 4 框還差 1 根。
        await using (var db = _database.NewContext())
        {
            var inventory = new InventoryService(db, _database.Time, NullLogger<InventoryService>.Instance);
            var (order, _) = await inventory.CreatePurchaseOrderAsync(new CreatePurchaseOrderRequest { ItemCode = Bar, Quantity = 5 }, null, default);
            await inventory.ReceivePurchaseOrderAsync(order.Id, default);
        }

        await AdvanceAsync(0);

        Assert.Equal([(3, 3), (2, 2), (2, 2), (3, 2)], await JobsAsync());
        await using (var db = _database.NewContext())
        {
            var statuses = await db.Lots.AsNoTracking().OrderBy(l => l.BinIndex).Select(l => l.Status).ToListAsync();
            Assert.Equal(
                [LotStatus.AwaitingTransport, LotStatus.AwaitingTransport, LotStatus.AwaitingTransport, LotStatus.AwaitingMaterial],
                statuses);
            Assert.Equal(3, await db.TransportTasks.CountAsync(t => t.ToStationCode == "cutting"));
        }

        var bar = await BalanceAsync(Bar);
        Assert.Equal((9, 9, 0), (bar.OnHand, bar.Reserved, bar.Available));
    }

    [Fact]
    public async Task Make_up_lot_takes_stock_blanks_when_there_are_enough_otherwise_it_is_cut()
    {
        int qcStep;
        await using (var db = _database.NewContext())
        {
            qcStep = (await db.ProcessSteps.SingleAsync(s => s.IsInspection)).Sequence;
        }

        // 兩框各 1 支在品管，都是尺寸不良（整框報廢，留在品管站、不擋下一框）：RD12 有現成備料、HX10 沒有。
        _database.Defects.Enqueue(DefectReason.Dimension, DefectReason.Dimension);
        await ArriveAtInspectionAsync("B-RD", "P30-RD12", qcStep);
        await ArriveAtInspectionAsync("B-HX", "P30-HX10", qcStep);
        await AdvanceAsync(0);
        await AdvanceAsync(1);
        await AdvanceAsync(1);

        // 報廢當下補做的框先在原料倉等料（HX10 的是最後一輪才報廢的）。
        await using (var db = _database.NewContext())
        {
            var waiting = await db.Lots.AsNoTracking().SingleAsync(l => l.IsMakeup && l.BatchNumber == "B-HX");
            Assert.Equal((LotStatus.AwaitingMaterial, 1), (waiting.Status, waiting.Quantity));
        }

        // 下一輪分配：HX10 的從圓棒切（1 根、切 13 支、多 12 支），RD12 的直接拿現成的備料送粗加工。
        await AdvanceAsync(0);

        await using var read = _database.NewContext();
        var hx = await read.Lots.AsNoTracking().SingleAsync(l => l.IsMakeup && l.BatchNumber == "B-HX");
        var rd = await read.Lots.AsNoTracking().SingleAsync(l => l.IsMakeup && l.BatchNumber == "B-RD");
        Assert.Equal((LotStatus.AwaitingTransport, 0), (hx.Status, hx.CurrentStepSequence!.Value));
        Assert.Equal((LotStatus.AwaitingTransport, 1), (rd.Status, rd.CurrentStepSequence!.Value));
        var job = await read.CuttingJobs.AsNoTracking().SingleAsync(j => j.LotId == hx.Id);
        Assert.Equal((1, 13, 12, true), (job.Bars, job.PiecesToCut, job.ExtraBlanks, job.IsReady));
        Assert.False(await read.CuttingJobs.AnyAsync(j => j.LotId == rd.Id));
        Assert.Equal(39, (await BalanceAsync("P30-RD12-U-BLK")).OnHand);
    }

    [Fact]
    public async Task Spec_must_exist_and_match_the_rod_diameter()
    {
        await using var scope = _database.NewScope();
        await Assert.ThrowsAsync<OpenFleet.Api.Production.RequestValidationException>(() =>
            scope.Production.CreateBatchAsync(10, null, null, "NOPE", default));
        await Assert.ThrowsAsync<OpenFleet.Api.Production.RequestValidationException>(() =>
            scope.Production.CreateBatchAsync(10, 24, null, "P30-RD12", default));

        // 沒有指定規格：用該桿徑的示範規格。
        var bins = await scope.Production.CreateBatchAsync(10, 24, [ProductParts.Die], default);
        Assert.Equal(("P24-SQ08", 24), (bins.Single().SpecCode, bins.Single().RodDiameterMm));
    }

    private async Task ArriveAtInspectionAsync(string batch, string spec, int qcStep)
    {
        await using var scope = _database.NewScope();
        var now = _database.Time.GetUtcNow();
        var lot = MaterialLot.Receive(ProductParts.UpperPunch, 1, "qc", qcStep, batch, 1, 1, 30, now, spec);
        lot.PlaceAt("qc", now);
        scope.Db.Lots.Add(lot);
        scope.Production.OnLotArrived(lot, now);
        await scope.Db.SaveChangesAsync();
        _database.Time.Advance(TimeSpan.FromMilliseconds(100));
    }

    /// <summary>模擬搬運：直接把框放到 <paramref name="station"/>，和車輛卸料一樣排隊等加工。</summary>
    private async Task DeliverAsync(string lotNumber, string station)
    {
        await using var scope = _database.NewScope();
        var now = _database.Time.GetUtcNow();
        var lot = await scope.Db.Lots.SingleAsync(l => l.LotNumber == lotNumber);
        lot.LoadOnto("agv-a", now);
        lot.UnloadAt(station, now);
        scope.Production.OnLotArrived(lot, now);
        await scope.Db.SaveChangesAsync();
    }

    private async Task AdvanceAsync(double seconds)
    {
        _database.Time.Advance(TimeSpan.FromSeconds(seconds));
        await using var scope = _database.NewScope();
        await scope.Production.AdvanceOperationsAsync(default);
    }

    private async Task AdjustBarsAsync(int delta)
    {
        await using var db = _database.NewContext();
        var inventory = new InventoryService(db, _database.Time, NullLogger<InventoryService>.Instance);
        await inventory.AdjustAsync(new StockAdjustmentRequest { ItemCode = Bar, Quantity = delta }, default);
    }

    private async Task<StockBalanceDto> BalanceAsync(string itemCode)
    {
        await using var db = _database.NewContext();
        var inventory = new InventoryService(db, _database.Time, NullLogger<InventoryService>.Instance);
        return (await inventory.GetSummaryAsync(default)).Balances.Single(b => b.ItemCode == itemCode);
    }

    private async Task<List<(int Bars, int Reserved)>> JobsAsync()
    {
        await using var db = _database.NewContext();
        return (await db.CuttingJobs.AsNoTracking().OrderBy(j => j.LotNumber).ToListAsync()).Select(j => (j.Bars, j.BarsReserved)).ToList();
    }
}
