using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;
using static OpenFleet.Tests.TestDatabase;

namespace OpenFleet.Tests;

/// <summary>
/// P7 第 1a 步：工序與工站分開。同一道工序有好幾站時，待派工的搬運派車時才選站（<see cref="StationSelector"/>）。
/// 這裡的例子是粗加工的兩台車床（粗加工 1、2 號機，各是一站）。
/// </summary>
public sealed class StationSelectorTests
{
    [Fact]
    public void Piece_station_prefers_the_better_changeover_then_a_ready_machine_then_the_shorter_route_then_the_code()
    {
        // 換線排名優先於路程。
        Assert.Equal("rough-2", StationSelector.PickPiece(
            [new StationSelector.PieceCandidate("rough-1", 4, true, 1), new StationSelector.PieceCandidate("rough-2", 1, true, 10)]));

        // 排名一樣時，機台馬上能做的優先於路程。
        Assert.Equal("rough-1", StationSelector.PickPiece(
            [new StationSelector.PieceCandidate("rough-2", 1, false, 1), new StationSelector.PieceCandidate("rough-1", 1, true, 30)]));

        // 其餘都一樣時，路程近的優先；路程也一樣時站代碼小的（ordinal）。
        Assert.Equal("rough-2", StationSelector.PickPiece(
            [new StationSelector.PieceCandidate("rough-1", 1, true, 30), new StationSelector.PieceCandidate("rough-2", 1, true, 10)]));
        Assert.Equal("rough-1", StationSelector.PickPiece(
            [new StationSelector.PieceCandidate("rough-2", 1, true, 10), new StationSelector.PieceCandidate("rough-1", 1, true, 10)]));

        Assert.Null(StationSelector.PickPiece([]));
    }

    [Fact]
    public void Furnace_fills_the_furnace_that_already_has_material_first_and_leaves_a_running_one_last()
    {
        // 爐前已經有料的優先（先湊滿一爐），即使路程比較遠。
        Assert.Equal("heat", StationSelector.PickFurnace(
            [new StationSelector.FurnaceCandidate("heat-b", false, 0, 1), new StationSelector.FurnaceCandidate("heat", false, 40, 30)]));

        // 爐前都有料時，料多的先。
        Assert.Equal("heat-b", StationSelector.PickFurnace(
            [new StationSelector.FurnaceCandidate("heat", false, 30, 1), new StationSelector.FurnaceCandidate("heat-b", false, 100, 30)]));

        // 沒在跑、爐前沒料的，排在有料的後面，正在跑的最後。
        Assert.Equal("heat-b", StationSelector.PickFurnace(
            [new StationSelector.FurnaceCandidate("heat", true, 200, 1), new StationSelector.FurnaceCandidate("heat-b", false, 0, 30)]));
        // 兩座都在跑：爐前料少的先。
        Assert.Equal("heat-b", StationSelector.PickFurnace(
            [new StationSelector.FurnaceCandidate("heat", true, 200, 1), new StationSelector.FurnaceCandidate("heat-b", true, 0, 30)]));
    }
}

public sealed class TransportDestinationTests
{
    [Fact]
    public void A_transport_to_a_step_has_no_station_until_it_is_bound_once()
    {
        var now = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
        var lot = MaterialLot.Receive(ProductParts.UpperPunch, 30, "raw-store", 1, "B-TEST", 1, 1, 30, now);
        var task = TransportTask.CreateToStep(lot, "raw-store", 1, TransportTaskKind.Replenishment, now);

        Assert.Equal((null, 1), (task.ToStationCode, task.ToStepSequence));
        task.BindDestination("rough");
        Assert.Equal(("rough", 1), (task.ToStationCode, task.ToStepSequence));
        Assert.Throws<InvalidOperationException>(() => task.BindDestination("rough-b"));
    }

    [Fact]
    public void A_transport_to_a_fixed_station_cannot_be_bound_to_another_station()
    {
        var now = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
        var lot = MaterialLot.Receive(ProductParts.UpperPunch, 30, "packing", 9, "B-TEST", 1, 1, 30, now);
        var task = TransportTask.Create(lot, "packing", "shipping", TransportTaskKind.WipTransfer, now);

        Assert.Throws<InvalidOperationException>(() => task.BindDestination("rough"));
        Assert.Equal("shipping", task.ToStationCode);
    }
}

/// <summary>
/// 粗加工 1 號機、2 號機各是一站（P7 第 1b 步）：待派工的框派車時才挑站。
/// 先看放得下的站，再看換線、機台能不能馬上做、路程；一樣近時站代碼小的先。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoughLatheChoiceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_bin_goes_to_the_lathe_that_is_ready_when_the_other_is_waiting_for_a_tool()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await CreateOneBinAsync(database);
        await MarkWaitingForToolAsync(database, "rough-1");

        await using var scope = database.NewScope();
        Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online("agv-a")], default));
        var task = await scope.Db.TransportTasks.SingleAsync();
        Assert.Equal(("rough-2", 1), (task.ToStationCode, task.ToStepSequence));
    }

    [Fact]
    public async Task A_bin_goes_to_the_other_lathe_when_the_first_one_has_no_room()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using (var db = database.NewContext())
        {
            (await db.Stations.SingleAsync(s => s.Code == "rough-1")).InboundCapacity = 0;
            await db.SaveChangesAsync();
        }

        await CreateOneBinAsync(database);

        await using var scope = database.NewScope();
        Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online("agv-a")], default));
        Assert.Equal("rough-2", (await scope.Db.TransportTasks.SingleAsync()).ToStationCode);
    }

    [Fact]
    public async Task A_bin_sent_back_to_waiting_chooses_its_lathe_again_at_the_next_dispatch()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await CreateOneBinAsync(database);

        // 兩台都空著時先看路程：1 號機離原料倉近，所以先送它。
        await using (var scope = database.NewScope())
        {
            Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online("agv-a")], default));
            Assert.Equal("rough-1", (await scope.Db.TransportTasks.SingleAsync()).ToStationCode);
        }

        // 車拒絕（不是因為忙碌）：任務回到待派工，目的站清掉，之後重新選。
        await using (var scope = database.NewScope())
        {
            var assignment = await scope.Db.Assignments.SingleAsync();
            Assert.True(await scope.Events.ProcessAsync("agv-a", Event(assignment, VehicleEventType.Rejected, "NoRoute"), default));
            var task = await scope.Db.TransportTasks.SingleAsync();
            Assert.Equal((TransportTaskStatus.Pending, (string?)null, 1), (task.Status, task.ToStationCode, task.ToStepSequence));
        }

        // 1 號機停機等刀：這次改送 2 號機。
        await MarkWaitingForToolAsync(database, "rough-1");
        await using (var scope = database.NewScope())
        {
            Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online("agv-a")], default));
            Assert.Equal("rough-2", (await scope.Db.TransportTasks.SingleAsync()).ToStationCode);
        }
    }

    private static async Task CreateOneBinAsync(TestDatabase database)
    {
        await using var scope = database.NewScope();
        var bins = await scope.Production.CreateBatchAsync(30, 30, [ProductParts.UpperPunch], default);
        Assert.Single(bins);
    }

    /// <summary>指定的粗加工機台停機等刀（刀具到壽命、新刀還沒送到）。</summary>
    private static async Task MarkWaitingForToolAsync(TestDatabase database, string machineCode)
    {
        await using var db = database.NewContext();
        foreach (var tool in await db.MachineTools.Where(t => t.MachineCode == machineCode).ToListAsync())
        {
            tool.WaitForSpare(database.Time.GetUtcNow());
        }

        await db.SaveChangesAsync();
    }
}

/// <summary>
/// P7 第 1a 步：整爐工序有好幾座爐時，預估的開爐每座爐各自算；已經排在別座爐前的框不算「還會有料來」。
/// 不經過資料庫：直接給 <see cref="ScheduleForecaster"/> 一份輸入（爐 heat 與 heat-b，都是工序 3）。
/// </summary>
public sealed class TwoFurnaceForecastTests
{
    [Fact]
    public void A_lot_queued_at_one_furnace_is_loaded_by_that_furnace()
    {
        var result = Forecast(testMode: false, Lot("B-U01", ScheduleForecaster.LotPhase.Queued, -10, "heat-b", 150));

        var run = Assert.Single(result.Runs);
        Assert.Equal(("heat-b", 0d, 150), (run.StationCode, Math.Round(run.Start, 3), run.Pieces));
    }

    [Fact]
    public void A_lot_already_waiting_at_another_furnace_is_not_counted_as_still_to_come()
    {
        // 測試模式：爐前有料、沒有其他料會再來就直接開爐。heat 前那一框已經排著，不算「還會來」，所以 heat-b 這一框不用等。
        var waitingAtHeat = Lot("B-U01", ScheduleForecaster.LotPhase.Queued, -10, "heat", 30);
        var waitingAtHeatB = Lot("B-U02", ScheduleForecaster.LotPhase.Queued, -5, "heat-b", 30);

        var result = Forecast(testMode: true, waitingAtHeat, waitingAtHeatB);

        var run = result.Runs.Single(r => r.StationCode == "heat-b");
        Assert.Equal((0d, 30), (Math.Round(run.Start, 3), run.Pieces));
        Assert.Equal(new[] { "B-U02" }, run.Lots.Select(l => l.LotNumber));
    }

    private static ScheduleForecaster.Result Forecast(bool testMode, params ScheduleForecaster.LotInput[] lots)
    {
        var input = new ScheduleForecaster.Input
        {
            Steps = [new ProcessStep { Sequence = 3, Code = "heat", Name = "淬火", Kind = ProcessStepKind.Batch, BatchMinPieces = 150, BatchMaxPieces = 300, BatchSeconds = 100 }],
            Settings = new ProductionSettings
            {
                SourceStationCode = "raw-store", BufferStationCode = "shipping", ToolStoreStationCode = "tool-store",
                TestFurnaceWaitSeconds = 60, TruckIntervalSeconds = 3600,
            },
            Stations = [new Station { Code = "heat", Name = "淬火", StepSequence = 3 }, new Station { Code = "heat-b", Name = "淬火 B 爐", StepSequence = 3 }],
            Machines = [],
            Runs = [],
            Lots = lots,
            Distance = (_, _) => 0,
        };
        return ScheduleForecaster.Run(input, new ScheduleForecaster.Rates(_ => 1, 0, 1, TestModeRules: testMode, TruckSpeed: 1));
    }

    private static ScheduleForecaster.LotInput Lot(string number, ScheduleForecaster.LotPhase phase, double queuedAt, string station, int pieces) => new()
    {
        Id = Guid.NewGuid(),
        LotNumber = number,
        BatchNumber = "B",
        PartCode = ProductParts.UpperPunch,
        RodDiameterMm = 30,
        Quantity = pieces,
        Step = 3,
        Phase = phase,
        QueuedAt = queuedAt,
        StationCode = station,
        Priority = SchedulePriority.None,
    };
}
