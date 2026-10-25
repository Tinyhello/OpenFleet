using OpenFleet.Contracts;
using OpenFleet.Api.Data;
using OpenFleet.Api.Production;

namespace OpenFleet.Tests;

/// <summary>
/// 預估中才送到下一道的框，派車的那一刻就照實際派送規則（<see cref="StationSelector"/>）指定站，之後只在那一站排隊與加工。
/// 不經過資料庫：直接給 <see cref="ScheduleForecaster"/> 一份輸入。
/// </summary>
public sealed class ForecastStationAssignmentTests
{
    [Fact]
    public void Lots_arriving_one_after_another_use_both_furnaces()
    {
        // 兩座爐都空著：第一框 15 秒送到，兩座爐一樣（沒在跑、爐前沒料、路程相同）取代碼小的 heat；
        // 第二框 30 秒送到時 heat 已經在跑，改送空著的 heat-b，不會堆在 heat 前面等。
        var result = FurnaceForecast([], [PreFurnaceLot("B-1", "B1", -2), PreFurnaceLot("B-2", "B2", -1)]);

        var heat = Assert.Single(result.Runs, r => r.StationCode == "heat");
        var heatB = Assert.Single(result.Runs, r => r.StationCode == "heat-b");
        Assert.Equal(("B-1", 15d), (string.Join(",", heat.Lots.Select(l => l.LotNumber)), Math.Round(heat.Start, 3)));
        Assert.Equal(("B-2", 30d), (string.Join(",", heatB.Lots.Select(l => l.LotNumber)), Math.Round(heatB.Start, 3)));
    }

    [Fact]
    public void A_lot_arriving_while_a_furnace_is_running_is_loaded_by_the_idle_furnace()
    {
        var running = FurnaceLot("B-R", "BR", ScheduleForecaster.LotPhase.InFurnace, 0, null);
        var result = FurnaceForecast(
            [new ScheduleForecaster.RunInput { StationCode = "heat", StartedAt = -10, RemainingSeconds = 1000, LotIds = [running.Id] }],
            [running, PreFurnaceLot("B-1", "B1", -2)]);

        var planned = Assert.Single(result.Runs, r => !r.Running);
        Assert.Equal(("heat-b", 15d), (planned.StationCode, Math.Round(planned.Start, 3)));
    }

    [Fact]
    public void Three_rough_lathes_share_arriving_lots_the_way_the_station_selector_does()
    {
        // 切料站每 15 秒送出一框（同批次的 L4 排名最好，第二個出來）。批次不同的框：1 號機忙著時改去能馬上做的機台，三台各一框；
        // 和 1 號機上一框同批次的 L4 排名最好（不用換線也同批次），即使別台空著，也排在 1 號機前，不改去別台。
        var result = LatheForecast(
            Cut("L1", "B1", rough: 100), Cut("L4", "B1", rough: 1), Cut("L2", "B2", rough: 1), Cut("L3", "B3", rough: 1));

        var bins = result.Bins.Where(b => b.MachineCode.StartsWith("rough-")).ToDictionary(b => b.Lot.LotNumber, b => b.MachineCode);
        Assert.Equal("rough-1", bins["L1"]);
        Assert.Equal("rough-1", bins["L4"]);
        Assert.Equal("rough-2", bins["L2"]);
        Assert.Equal("rough-3", bins["L3"]);
    }

    [Fact]
    public void A_lot_already_queued_at_a_lathe_stays_with_that_lathe()
    {
        // 2 號機到 500 秒才空；排在 2 號機前的框不會改給空著的 1 號機。
        var lot = new ScheduleForecaster.LotInput
        {
            Id = Guid.NewGuid(), LotNumber = "Q", BatchNumber = "BQ", PartCode = ProductParts.UpperPunch, RodDiameterMm = 30, Quantity = 30,
            Step = 1, Phase = ScheduleForecaster.LotPhase.Queued, QueuedAt = -5, StationCode = "rough-2", Priority = SchedulePriority.None,
        };
        var result = LatheForecast([lot], ("rough-2", 500));

        var bin = Assert.Single(result.Bins);
        Assert.Equal(("rough-2", 500d), (bin.MachineCode, Math.Round(bin.Start, 3)));
    }

    private static ScheduleForecaster.Result LatheForecast(params ScheduleForecaster.LotInput[] lots) => LatheForecast(lots, null);

    private static ScheduleForecaster.Result LatheForecast(ScheduleForecaster.LotInput[] lots, (string Machine, double Seconds)? stopSeconds)
    {
        var machines = new List<ScheduleForecaster.MachineInput> { new() { Code = "cut", StationCode = "cut", Number = 1, StepSequence = 0 } };
        machines.AddRange(Enumerable.Range(1, 3).Select(n => new ScheduleForecaster.MachineInput
        {
            Code = $"rough-{n}", StationCode = $"rough-{n}", Number = n, StepSequence = 1,
            StopSeconds = stopSeconds is { } stop && stop.Machine == $"rough-{n}" ? stop.Seconds : 0,
        }));
        var input = new ScheduleForecaster.Input
        {
            Steps =
            [
                new ProcessStep { Sequence = 0, Code = "cut", Name = "切料", Kind = ProcessStepKind.Piece, SecondsPerPiece = 0.5 },
                new ProcessStep { Sequence = 1, Code = "rough", Name = "粗加工", Kind = ProcessStepKind.Piece, SecondsPerPiece = 1, UsesTool = true, ToolLifePieces = 500 },
            ],
            Settings = Settings(),
            Stations =
            [
                new Station { Code = "cut", Name = "切料", StepSequence = 0 },
                new Station { Code = "rough-1", Name = "粗加工 1 號機", StepSequence = 1 },
                new Station { Code = "rough-2", Name = "粗加工 2 號機", StepSequence = 1 },
                new Station { Code = "rough-3", Name = "粗加工 3 號機", StepSequence = 1 },
            ],
            Machines = machines,
            Runs = [],
            Lots = lots,
            Distance = (_, _) => 0,
        };
        return ScheduleForecaster.Run(input, new ScheduleForecaster.Rates(_ => 1, 0, 1, TestModeRules: false, TruckSpeed: 1));
    }

    private static ScheduleForecaster.LotInput Cut(string number, string batch, double rough) => new()
    {
        Id = Guid.NewGuid(), LotNumber = number, BatchNumber = batch, PartCode = ProductParts.UpperPunch, RodDiameterMm = 30, Quantity = 30,
        Step = 0, Phase = ScheduleForecaster.LotPhase.Queued, QueuedAt = -10, StationCode = "cut", Priority = SchedulePriority.None,
        SecondsPerPiece = new Dictionary<string, double> { ["rough"] = rough },
    };

    /// <summary>淬火前一道（工序 2，單機、一框 150 支約 15 秒）加兩座爐（工序 3，下限 150 支）。</summary>
    private static ScheduleForecaster.Result FurnaceForecast(
        IReadOnlyList<ScheduleForecaster.RunInput> runs, IReadOnlyList<ScheduleForecaster.LotInput> lots)
    {
        var input = new ScheduleForecaster.Input
        {
            Steps =
            [
                new ProcessStep { Sequence = 2, Code = "pre", Name = "前一道", Kind = ProcessStepKind.Piece, SecondsPerPiece = 0.1 },
                new ProcessStep { Sequence = 3, Code = "heat", Name = "淬火", Kind = ProcessStepKind.Batch, BatchMinPieces = 150, BatchMaxPieces = 300, BatchSeconds = 100 },
            ],
            Settings = Settings(),
            Stations =
            [
                new Station { Code = "pre", Name = "前一道", StepSequence = 2 },
                new Station { Code = "heat", Name = "淬火", StepSequence = 3 },
                new Station { Code = "heat-b", Name = "淬火 B 爐", StepSequence = 3 },
            ],
            Machines = [new ScheduleForecaster.MachineInput { Code = "pre", StationCode = "pre", Number = 1, StepSequence = 2 }],
            Runs = runs,
            Lots = lots,
            Distance = (_, _) => 0,
        };
        return ScheduleForecaster.Run(input, new ScheduleForecaster.Rates(_ => 1, 0, 1, TestModeRules: false, TruckSpeed: 1));
    }

    private static ScheduleForecaster.LotInput PreFurnaceLot(string number, string batch, double queuedAt) =>
        FurnaceLot(number, batch, ScheduleForecaster.LotPhase.Queued, queuedAt, "pre", step: 2);

    private static ScheduleForecaster.LotInput FurnaceLot(
        string number, string batch, ScheduleForecaster.LotPhase phase, double queuedAt, string? station, int step = 3) => new()
    {
        Id = Guid.NewGuid(), LotNumber = number, BatchNumber = batch, PartCode = ProductParts.UpperPunch, RodDiameterMm = 30, Quantity = 150,
        Step = step, Phase = phase, QueuedAt = queuedAt, StationCode = station, Priority = SchedulePriority.None,
    };

    private static ProductionSettings Settings() => new()
    {
        SourceStationCode = "raw-store", BufferStationCode = "shipping", ToolStoreStationCode = "tool-store",
        TestFurnaceWaitSeconds = 60, TruckIntervalSeconds = 3600,
    };
}
