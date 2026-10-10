using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;
using static OpenFleet.Tests.TestDatabase;

namespace OpenFleet.Tests;

/// <summary>
/// P4：批次分框、機床換線，以及後段的整爐工序（淬火、回火、電鍍）與拋光。
/// 測試用的時間：淬火 100 秒、回火 200 秒、電鍍 60 秒、逐件工序每件 2 秒；
/// 開爐門檻用示範值（淬火與回火 150～300 支；電鍍一爐 30 mm 40 支、24 mm 56 支，有料就開）。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BackEndProcessTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(postgres);
        await using var db = _database.NewContext();
        await db.ProcessSteps.Where(s => s.Kind == ProcessStepKind.Piece).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecondsPerPiece, 2d));
        await db.ProcessSteps.Where(s => s.StationCode == "heat").ExecuteUpdateAsync(s => s.SetProperty(x => x.BatchSeconds, 100d));
        await db.ProcessSteps.Where(s => s.StationCode == "temper").ExecuteUpdateAsync(s => s.SetProperty(x => x.BatchSeconds, 200d));
        await db.ProcessSteps.Where(s => s.StationCode == "plating").ExecuteUpdateAsync(s => s.SetProperty(x => x.BatchSeconds, 60d));
        await db.ProductionSettings.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.PieceHandlingSeconds, 0d)
            .SetProperty(x => x.ToolDeliveryEnabled, false));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Batch_is_split_into_bins_by_rod_diameter_and_each_bin_gets_its_own_transport()
    {
        await using var scope = _database.NewScope();

        // 桿徑 30 mm 一框 30 支：100 支分成 30、30、30、10。
        var thick = await scope.Production.CreateBatchAsync(100, 30, default);
        Assert.Equal(new[] { 30, 30, 30, 10 }, thick.Select(b => b.Quantity));
        Assert.Equal(new[] { (1, 4), (2, 4), (3, 4), (4, 4) }, thick.Select(b => (b.BinIndex, b.BinCount)));
        Assert.Single(thick.Select(b => b.BatchNumber).Distinct());
        Assert.Equal(thick.Select(b => $"{b.BatchNumber}-{b.BinIndex:00}"), thick.Select(b => b.LotNumber));

        // 桿徑 24 mm 一框 45 支：100 支分成 45、45、10。
        var thin = await scope.Production.CreateBatchAsync(100, 24, default);
        Assert.Equal(new[] { 45, 45, 10 }, thin.Select(b => b.Quantity));
        Assert.NotEqual(thick[0].BatchNumber, thin[0].BatchNumber);

        // 每一框各一趟送往第一道工序的搬運。
        var tasks = await scope.Db.TransportTasks.ToListAsync();
        Assert.Equal(7, tasks.Count);
        Assert.All(tasks, t => Assert.Equal(("raw-store", "rough", TransportTaskKind.Replenishment), (t.FromStationCode, t.ToStationCode, t.Kind)));

        // 省略時用設定的數量與 30 mm；一個批次最多 300 支；桿徑只有 24 與 30。
        Assert.Equal((30, 30), ((await scope.Production.CreateBatchAsync(null, null, default)).Single().Quantity, 30));
        Assert.Equal(10, (await scope.Production.CreateBatchAsync(300, 30, default)).Count);
        await Assert.ThrowsAsync<RequestValidationException>(() => scope.Production.CreateBatchAsync(301, 30, default));
        await Assert.ThrowsAsync<RequestValidationException>(() => scope.Production.CreateBatchAsync(30, 25, default));
    }

    [Fact]
    public async Task Bins_are_fed_to_the_machine_one_at_a_time_even_when_a_bin_is_larger_than_the_inbound_capacity()
    {
        await using (var scope = _database.NewScope())
        {
            await scope.Production.CreateBatchAsync(90, 24, default);
        }

        // 粗加工的備料區容量是 30 支，但 45 支的一框至少要放得下一框；第二框要等。
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online("agv-a"), Online("agv-b")], default));
            Assert.Equal(45, (await scope.Db.TransportTasks.SingleAsync(t => t.Status == TransportTaskStatus.Assigned)).Quantity);
        }
    }

    [Fact]
    public async Task Quench_waits_for_the_minimum_load_then_sends_every_bin_to_the_temper_furnace()
    {
        // 四框共 120 支到了淬火站：不到 150 支，不開爐，一直等。
        await ArriveAsync("heat", 3, "B-A", 30, 30, 30, 30);
        await AdvanceAsync(0);
        _database.Time.Advance(TimeSpan.FromHours(5));
        await AdvanceAsync(0);
        var heat = await StationAsync("heat");
        Assert.Equal((120, false, 150, 300), (heat.InboundQuantity, heat.Batch!.Running, heat.Batch.MinPieces, heat.Batch.MaxPieces));
        Assert.Null(heat.Tool);

        // 另一個批次的一框到了，湊滿 150 支：開爐，不同批次混在同一爐。
        await ArriveAsync("heat", 3, "B-B", 30);
        await AdvanceAsync(0);
        heat = await StationAsync("heat");
        Assert.Equal((0, true, 150, "淬火", 0d), (heat.InboundQuantity, heat.Batch!.Running, heat.Batch.Quantity, heat.Batch.StageName, heat.Batch.StageProgress));

        // 淬火 100 秒做到一半。
        await AdvanceAsync(50);
        heat = await StationAsync("heat");
        Assert.Equal(("淬火", 0.5, 50d), (heat.Batch!.StageName, heat.Batch.StageProgress, heat.Batch.RemainingSeconds));

        // 測試模式、淬火站 10 倍：2 秒走掉 20 秒的工序時間。之前已經過的時間不受倍率影響；
        // 別站的倍率（粗加工 1000 倍）和這一站無關。
        await ConfigureAsync(settings =>
        {
            settings.TestMode = true;
            settings.Steps.Single(s => s.StationCode == "heat").TestSpeed = 10;
            settings.Steps.Single(s => s.StationCode == "rough").TestSpeed = 1000;
        });
        await AdvanceAsync(2);
        heat = await StationAsync("heat");
        Assert.Equal((0.7, 30d), (heat.Batch!.StageProgress, heat.Batch.RemainingSeconds));

        // 淬火做完：整爐一起放行。回火是另一座爐，每一框各自等待搬運過去。
        await AdvanceAsync(3);
        heat = await StationAsync("heat");
        Assert.Equal((false, 150), (heat.Batch!.Running, heat.OutboundQuantity));
        await using var scope = _database.NewScope();
        var lots = await scope.Db.Lots.ToListAsync();
        Assert.All(lots, l => Assert.Equal((LotStatus.AwaitingTransport, "heat", 4), (l.Status, l.StationCode, l.CurrentStepSequence)));
        Assert.Equal(5, await scope.Db.TransportTasks.CountAsync(t => t.FromStationCode == "heat" && t.ToStationCode == "temper"));
        Assert.All(await scope.Db.Operations.ToListAsync(), o => Assert.Equal((OperationStatus.Completed, o.Quantity), (o.Status, o.PiecesCompleted)));
    }

    [Fact]
    public async Task Temper_is_a_separate_furnace_with_its_own_load_and_run()
    {
        // 淬火出爐的框一框一框搬過來：不到 150 支不開爐。
        await ArriveAsync("temper", 4, "B-A", 30, 30, 30, 30);
        await AdvanceAsync(0);
        Assert.False((await StationAsync("temper")).Batch!.Running);

        await ArriveAsync("temper", 4, "B-A", 30);
        await AdvanceAsync(0);
        var temper = await StationAsync("temper");
        Assert.Equal((true, 150, "回火"), (temper.Batch!.Running, temper.Batch.Quantity, temper.Batch.StageName));

        // 回火 200 秒做完，每一框各自等待搬運到電鍍。
        await AdvanceAsync(200);
        await using var scope = _database.NewScope();
        Assert.Equal(5, await scope.Db.TransportTasks.CountAsync(t => t.FromStationCode == "temper" && t.ToStationCode == "plating"));
    }

    [Fact]
    public async Task Furnace_takes_whole_bins_up_to_the_maximum_and_leaves_the_rest_for_the_next_run()
    {
        // 十一框共 330 支：一爐最多 300 支，第十一框留到下一爐。
        await ArriveAsync("heat", 3, "B-A", 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30);
        await AdvanceAsync(0);

        var heat = await StationAsync("heat");
        Assert.Equal((true, 300, 30), (heat.Batch!.Running, heat.Batch.Quantity, heat.InboundQuantity));

        // 這一爐還在跑的時候不會開第二爐；跑完之後剩下的 30 支不到下限，繼續等。
        await AdvanceAsync(300);
        heat = await StationAsync("heat");
        Assert.Equal((false, 30, 300), (heat.Batch!.Running, heat.InboundQuantity, heat.OutboundQuantity));
    }

    [Fact]
    public async Task Plating_load_is_limited_per_rod_diameter_and_the_two_diameters_can_be_mixed()
    {
        // 一爐 30 mm 最多 40 支、24 mm 最多 56 支。30 mm 的 30 支占 3/4 爐；
        // 24 mm 的 45 支（占 45/56）放不下，留到下一爐；後到的 24 mm 14 支剛好占剩下的 1/4 爐，混在同一爐。
        await ArriveRodAsync("plating", 5, "B-A", 30, 30);
        await ArriveRodAsync("plating", 5, "B-B", 24, 45);
        await ArriveRodAsync("plating", 5, "B-C", 24, 14);
        await AdvanceAsync(0);
        var plating = await StationAsync("plating");
        Assert.Equal((true, 44, 45, "電鍍"), (plating.Batch!.Running, plating.Batch.Quantity, plating.InboundQuantity, plating.Batch.StageName));

        // 電鍍 60 秒做完，兩框各自等待搬運到拋光；有料就開，所以留下的那一框接著開下一爐。
        await AdvanceAsync(60);
        plating = await StationAsync("plating");
        Assert.Equal((true, 45, 0, 44), (plating.Batch!.Running, plating.Batch.Quantity, plating.InboundQuantity, plating.OutboundQuantity));
        await using var scope = _database.NewScope();
        Assert.Equal(2, await scope.Db.TransportTasks.CountAsync(t => t.FromStationCode == "plating" && t.ToStationCode == "polish"));
    }

    [Fact]
    public async Task Plating_takes_only_one_full_bin_of_the_same_diameter_because_bins_are_not_split()
    {
        // 30 mm 一框 30 支，一爐最多 40 支：第二框放不下（框不拆開），一爐只有一框。
        await ArriveRodAsync("plating", 5, "B-A", 30, 30, 30);
        await AdvanceAsync(0);
        var plating = await StationAsync("plating");
        Assert.Equal((true, 30, 30), (plating.Batch!.Running, plating.Batch.Quantity, plating.InboundQuantity));
    }

    [Fact]
    public async Task Lathe_changes_over_when_the_rod_diameter_changes_and_first_takes_bins_that_need_no_changeover()
    {
        // 換線 60 秒。到站順序：30 mm、24 mm、30 mm，各 5 支（每件 2 秒）。
        await ConfigureAsync(settings => settings.ChangeoverSeconds = 60);
        await ArriveRodAsync("rough", 1, "B-A", 30, 5);
        await ArriveRodAsync("rough", 1, "B-B", 24, 5);
        await ArriveRodAsync("rough", 1, "B-C", 30, 5);
        await AdvanceAsync(0);

        // 這一站的第一框不用換線。
        Assert.Equal("B-A", await RunningBatchAsync("rough"));
        Assert.False((await StationAsync("rough")).Machining!.ChangingOver);

        // 做完搬走後，先做同桿徑的 B-C（不用換線），不是先到的 B-B。
        await AdvanceAsync(10);
        Assert.Equal(1, await DispatchAsync());
        await _database.CompleteDeliveryAsync("agv-a");
        await AdvanceAsync(0);
        Assert.Equal("B-C", await RunningBatchAsync("rough"));
        Assert.False((await StationAsync("rough")).Machining!.ChangingOver);

        // 輪到 24 mm 的 B-B：先換線。測試模式下換線也依這一站的倍率加快（10 倍：60 秒變 6 秒，每件 0.2 秒）。
        await AdvanceAsync(10);
        Assert.Equal(1, await DispatchAsync());
        await _database.CompleteDeliveryAsync("agv-a");
        await ConfigureAsync(settings =>
        {
            settings.TestMode = true;
            settings.Steps[0].TestSpeed = 10;
        });
        await AdvanceAsync(0);
        Assert.Equal("B-B", await RunningBatchAsync("rough"));
        var rough = await StationAsync("rough");
        Assert.Equal((true, 0d, 0), (rough.Machining!.ChangingOver, rough.Machining.ChangeoverProgress, rough.Machining.PiecesCompleted));

        await AdvanceAsync(3);
        rough = await StationAsync("rough");
        Assert.Equal((true, 0.5, 0), (rough.Machining!.ChangingOver, rough.Machining.ChangeoverProgress, rough.Machining.PiecesCompleted));

        // 換線完成後才開始做第一件：再 3.5 秒，換線用掉 3 秒，剩下 0.5 秒做完 2 件。
        await AdvanceAsync(3.5);
        rough = await StationAsync("rough");
        Assert.Equal((false, 2), (rough.Machining!.ChangingOver, rough.Machining.PiecesCompleted));

        // 拋光不是機床：桿徑不同也不換線。
        await ArriveRodAsync("polish", 6, "B-D", 30, 5);
        await AdvanceAsync(10);
        await ArriveRodAsync("polish", 6, "B-E", 24, 5);
        await AdvanceAsync(0);
        Assert.False((await StationAsync("polish")).Machining?.ChangingOver ?? false);
    }

    [Fact]
    public async Task Polishing_keeps_bins_of_the_same_batch_together_and_has_no_tool()
    {
        // 到站順序：A 的第一框、B 的一框、A 的第二框。
        await ArriveAsync("polish", 6, "B-A", 10);
        await ArriveAsync("polish", 6, "B-B", 10);
        await ArriveAsync("polish", 6, "B-A", 10);
        await AdvanceAsync(0);
        Assert.Equal("B-A", await RunningBatchAsync());

        // 拋光每支 2 秒，沒有刀具：做再多也不會換刀。
        await AdvanceAsync(20);
        var polish = await StationAsync("polish");
        Assert.Equal((10, null), (polish.OutboundQuantity, polish.Tool));

        // 做完的那一框搬到品管站入口暫存（流程目前的終點）；接著做的是同批次的另一框，不是先到的 B。
        Assert.Equal(1, await DispatchAsync());
        await _database.CompleteDeliveryAsync("agv-a");
        await AdvanceAsync(0);
        Assert.Equal("B-A", await RunningBatchAsync());
        await using (var scope = _database.NewScope())
        {
            var done = await scope.Db.Lots.SingleAsync(l => l.Status == LotStatus.Staged);
            Assert.Equal(("qc", null), (done.StationCode, done.CurrentStepSequence));
        }

        // A 都做完了才輪到 B。
        await AdvanceAsync(20);
        Assert.Equal(1, await DispatchAsync());
        await _database.CompleteDeliveryAsync("agv-a");
        await AdvanceAsync(0);
        Assert.Equal("B-B", await RunningBatchAsync());
    }

    /// <summary>把幾框桿徑 30 mm 的物料直接放到某站排隊（省略前面的工序）。</summary>
    private Task ArriveAsync(string station, int step, string batchNumber, params int[] bins) =>
        ArriveRodAsync(station, step, batchNumber, 30, bins);

    private async Task ArriveRodAsync(string station, int step, string batchNumber, int rodDiameterMm, params int[] bins)
    {
        await using var scope = _database.NewScope();
        var now = _database.Time.GetUtcNow();
        var existing = await scope.Db.Lots.CountAsync(l => l.BatchNumber == batchNumber);
        for (var i = 0; i < bins.Length; i++)
        {
            var lot = MaterialLot.Receive("upper-punch", bins[i], station, step, batchNumber, existing + i + 1, existing + bins.Length, rodDiameterMm, now);
            lot.PlaceAt(station, now);
            scope.Db.Lots.Add(lot);
            scope.Production.OnLotArrived(lot, now.AddMilliseconds(i));
        }

        await scope.Db.SaveChangesAsync();
        _database.Time.Advance(TimeSpan.FromMilliseconds(100));
    }

    /// <summary>時間前進 <paramref name="seconds"/> 秒並把加工推進到現在。</summary>
    private async Task AdvanceAsync(double seconds)
    {
        _database.Time.Advance(TimeSpan.FromSeconds(seconds));
        await using var scope = _database.NewScope();
        await scope.Production.AdvanceOperationsAsync(default);
    }

    private async Task ConfigureAsync(Action<ProductionSettingsDto> change)
    {
        await using var scope = _database.NewScope();
        var settings = await scope.Production.GetSettingsAsync(default);
        change(settings);
        await scope.Production.UpdateSettingsAsync(settings, default);
    }

    private async Task<int> DispatchAsync()
    {
        await using var scope = _database.NewScope();
        return await scope.Transport.DispatchPendingAsync([Online("agv-a")], default);
    }

    /// <summary>正在加工的批次；指定站點時只看那一站（搬走的框會在下一站開始加工）。</summary>
    private async Task<string> RunningBatchAsync(string? station = null)
    {
        await using var scope = _database.NewScope();
        return await scope.Db.Operations
            .Where(o => o.Status == OperationStatus.Running && (station == null || o.StationCode == station))
            .Select(o => o.Lot.BatchNumber)
            .SingleAsync();
    }

    private async Task<StationStatusDto> StationAsync(string code)
    {
        await using var scope = _database.NewScope();
        return (await scope.Production.GetStationStatusAsync(default)).Single(s => s.Code == code);
    }
}
