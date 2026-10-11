using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Contracts;
using static OpenFleet.Tests.TestDatabase;

namespace OpenFleet.Tests;

/// <summary>
/// P4：品管逐支全檢，檢完在品管站分框；表面不良回拋光、鍍層不良回電鍍、尺寸不良報廢，同一支最多反工一次。
/// 測試用的時間：檢驗與拋光每支 2 秒、沒有機台取放。每一支的檢驗結果由測試指定（沒有指定的都是合格）。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QualityInspectionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const int Plating = 5, Polish = 6, Inspection = 7, Packing = 8;

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
    public async Task Inspected_bin_is_split_into_passed_rework_by_reason_and_scrap()
    {
        // 十支裡第 2 支表面不良、第 4 支鍍層不良、第 5 支尺寸不良。
        var lot = await ArriveAsync("qc", Inspection, 10);
        _database.Defects.Enqueue(null, DefectReason.Surface, null, DefectReason.Plating, DefectReason.Dimension);
        await AdvanceAsync(0);

        // 逐支檢驗：每支 2 秒，檢到一半時還沒有任何判定結果寫出去。
        await AdvanceAsync(10);
        var qc = await StationAsync("qc");
        Assert.Equal((true, 5, 10), (qc.IsInspection, qc.Machining!.PiecesCompleted, qc.Machining.Quantity));
        Assert.Equal(0, (await SummaryAsync()).Inspected);

        await AdvanceAsync(10);
        await using var scope = _database.NewScope();
        var bins = await scope.Db.Lots.OrderBy(l => l.LotNumber).ToListAsync();

        // 原本的框留下合格的 7 支，送往包裝站。
        Assert.Equal(new[] { lot.LotNumber, $"{lot.LotNumber}-M1", $"{lot.LotNumber}-RP", $"{lot.LotNumber}-RS" }, bins.Select(b => b.LotNumber));
        var makeup = bins[1];
        bins.RemoveAt(1);
        Assert.Equal((7, LotStatus.AwaitingTransport, "qc", Packing, 0), (bins[0].Quantity, bins[0].Status, bins[0].StationCode, bins[0].CurrentStepSequence!.Value, bins[0].ReworkCount));

        // 要反工的另外裝框，批次編號相同：鍍層不良回電鍍、表面不良回拋光，各記一次反工。
        Assert.All(bins, b => Assert.Equal(lot.BatchNumber, b.BatchNumber));
        Assert.Equal((1, LotStatus.AwaitingTransport, "qc", Plating, 1), (bins[1].Quantity, bins[1].Status, bins[1].StationCode, bins[1].CurrentStepSequence!.Value, bins[1].ReworkCount));
        Assert.Equal((1, LotStatus.AwaitingTransport, "qc", Polish, 1), (bins[2].Quantity, bins[2].Status, bins[2].StationCode, bins[2].CurrentStepSequence!.Value, bins[2].ReworkCount));

        var tasks = await scope.Db.TransportTasks.Where(t => t.FromStationCode == "qc").ToListAsync();
        Assert.Equal(new[] { ("packing", 7), ("plating", 1), ("polish", 1) }, tasks.Select(t => (t.ToStationCode, t.Quantity)).Order());

        // 尺寸不良的那一支報廢：留下位置異動紀錄，不在任何一框裡。
        var scrap = await scope.Db.InventoryMovements.SingleAsync(m => m.Reason == "Scrapped");
        Assert.Equal((lot.Id, 1, "station:qc", "scrap"), (scrap.LotId, scrap.Quantity, scrap.From, scrap.To));

        var result = await scope.Db.InspectionResults.SingleAsync();
        Assert.Equal((10, 7, 2, 1), (result.Inspected, result.Passed, result.Reworked, result.Scrapped));
        Assert.Equal((1, 1, 1), (result.SurfaceDefects, result.PlatingDefects, result.DimensionDefects));

        // 缺的自動補做：報廢 1 支，當下就在原料倉補投 1 支同一種零件（同批次），從第一道工序重做。
        Assert.Equal((1, true, LotStatus.AwaitingTransport, "raw-store", 1, 0, lot.PartCode, lot.RodDiameterMm),
            (makeup.Quantity, makeup.IsMakeup, makeup.Status, makeup.StationCode, makeup.CurrentStepSequence!.Value, makeup.ReworkCount, makeup.PartCode, makeup.RodDiameterMm));
        var replenish = await scope.Db.TransportTasks.SingleAsync(t => t.LotId == makeup.Id);
        Assert.Equal(("raw-store", "rough", TransportTaskKind.Replenishment), (replenish.FromStationCode, replenish.ToStationCode, replenish.Kind));
        Assert.Equal(1, (await scope.Db.InventoryMovements.SingleAsync(m => m.Reason == "MakeupReceived")).Quantity);
    }

    [Fact]
    public async Task Reworked_pieces_come_back_for_inspection_and_are_scrapped_if_they_fail_again()
    {
        // 兩支都是表面不良：沒有合格的，原本的框整框回拋光重做（不另外分框）。
        var lot = await ArriveAsync("qc", Inspection, 2);
        _database.Defects.Enqueue(DefectReason.Surface, DefectReason.Surface);
        await AdvanceAsync(0);
        await AdvanceAsync(4);
        Assert.Equal((2, LotStatus.AwaitingTransport, "qc", Polish, 1), await StateAsync(lot.Id));

        // 搬回拋光、重做、再搬回品管。
        await DeliverAsync();
        await AdvanceAsync(0);
        await AdvanceAsync(4);
        await DeliverAsync();
        Assert.Equal((2, LotStatus.Queued, "qc", Inspection, 1), await StateAsync(lot.Id));

        // 複檢：一支合格，另一支又是表面不良。已經反工過一次（上限），所以報廢，不再反工。
        _database.Defects.Enqueue(null, DefectReason.Surface);
        await AdvanceAsync(0);
        await AdvanceAsync(4);
        Assert.Equal((1, LotStatus.AwaitingTransport, "qc", Packing, 1), await StateAsync(lot.Id));

        await using var scope = _database.NewScope();
        // 除了原本的框，只多了補做報廢那 1 支的框（框號記下是第 2 次檢驗時報廢的）。
        Assert.Equal(new[] { lot.LotNumber, $"{lot.LotNumber}-M2" }, await scope.Db.Lots.OrderBy(l => l.LotNumber).Select(l => l.LotNumber).ToListAsync());
        Assert.Equal(new[] { "packing", "rough" },
            await scope.Db.TransportTasks.Where(t => t.Status == TransportTaskStatus.Pending).Select(t => t.ToStationCode).OrderBy(c => c).ToListAsync());
        var summary = await scope.Production.GetInspectionSummaryAsync(default);
        Assert.Equal((4, 1, 2, 1, 3), (summary.Inspected, summary.Passed, summary.Reworked, summary.Scrapped, summary.SurfaceDefects));
    }

    [Fact]
    public async Task Bin_with_nothing_left_is_marked_scrapped_and_stays_at_the_inspection_station()
    {
        var lot = await ArriveAsync("qc", Inspection, 2);
        _database.Defects.Enqueue(DefectReason.Dimension, DefectReason.Dimension);
        await AdvanceAsync(0);
        await AdvanceAsync(4);

        Assert.Equal((2, LotStatus.Scrapped, "qc", null, 0), await StateAsync(lot.Id));
        await using var scope = _database.NewScope();
        // 唯一的搬運是補做的那 2 支（原料倉 → 粗加工）；報廢的框本身不搬。
        var task = await scope.Db.TransportTasks.SingleAsync();
        Assert.Equal((2, "raw-store", "rough"), (task.Quantity, task.FromStationCode, task.ToStationCode));

        // 報廢的框不占用品管站：下一框照常開始檢驗。
        await ArriveAsync("qc", Inspection, 3);
        await AdvanceAsync(0);
        Assert.Equal(3, (await StationAsync("qc")).Machining!.Quantity);
    }

    [Fact]
    public async Task Rework_can_be_turned_off_so_that_every_defect_is_scrapped()
    {
        await using (var scope = _database.NewScope())
        {
            var settings = await scope.Production.GetSettingsAsync(default);
            settings.MaxReworkCount = 0;
            await scope.Production.UpdateSettingsAsync(settings, default);
        }

        var lot = await ArriveAsync("qc", Inspection, 3);
        _database.Defects.Enqueue(DefectReason.Surface, DefectReason.Plating);
        await AdvanceAsync(0);
        await AdvanceAsync(6);

        Assert.Equal((1, LotStatus.AwaitingTransport, "qc", Packing, 0), await StateAsync(lot.Id));
        var summary = await SummaryAsync();
        Assert.Equal((3, 1, 0, 2), (summary.Inspected, summary.Passed, summary.Reworked, summary.Scrapped));
    }

    /// <summary>把一框物料直接放到某站排隊（省略前面的工序）。</summary>
    private async Task<MaterialLot> ArriveAsync(string station, int step, int quantity)
    {
        await using var scope = _database.NewScope();
        var now = _database.Time.GetUtcNow();
        var lot = MaterialLot.Receive("upper-punch", quantity, station, step, $"B-{Guid.NewGuid():N}"[..10], 1, 1, 30, now);
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

    /// <summary>派出等待中的那一趟搬運並送達。</summary>
    private async Task DeliverAsync()
    {
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online("agv-a")], default));
        }

        await _database.CompleteDeliveryAsync("agv-a");
    }

    private async Task<(int Quantity, LotStatus Status, string? Station, int? Step, int ReworkCount)> StateAsync(Guid lotId)
    {
        await using var scope = _database.NewScope();
        var lot = await scope.Db.Lots.SingleAsync(l => l.Id == lotId);
        return (lot.Quantity, lot.Status, lot.StationCode, lot.CurrentStepSequence, lot.ReworkCount);
    }

    private async Task<StationStatusDto> StationAsync(string code)
    {
        await using var scope = _database.NewScope();
        return (await scope.Production.GetStationStatusAsync(default)).Single(s => s.Code == code);
    }

    private async Task<InspectionSummaryDto> SummaryAsync()
    {
        await using var scope = _database.NewScope();
        return await scope.Production.GetInspectionSummaryAsync(default);
    }
}
