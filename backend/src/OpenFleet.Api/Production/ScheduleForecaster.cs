using OpenFleet.Api.Data;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Production;

/// <summary>
/// 排程預估（P6 第 3 步）：從現在廠內每一框、每台機台、每座爐的狀態出發，照加工推進的同一套規則往後推算
/// （各站挑框用 <see cref="MachineAssignment"/>、急單與交期用 <see cref="SchedulePriority"/>、開爐的下限與反工品的累積照
/// <see cref="ProductionService"/>），算出每台機台接下來做哪幾框、每座爐開哪幾爐、每一框什麼時候包裝完。
/// 純計算、不碰資料庫；時間都是「從現在起幾秒」。
/// 簡化（預估比實際樂觀的地方）：品管全部合格（不算反工與補做）；隨時有空車，搬運一趟是固定的估計時間（交接兩次加上路程），
/// 不算等車與交通；站的容量不會滿；停機等刀的機台當作新刀馬上到；等料的框一直等（不預估採購什麼時候到）。
/// </summary>
public sealed class ScheduleForecaster
{
    /// <summary>
    /// 預估用的速度。<see cref="StepSpeed"/> 是每一站的倍率（正常速度都是 1）；<see cref="HandoverSeconds"/> 是交接一框的時間；
    /// <see cref="TestModeRules"/> 為 true 時照測試模式的開爐規則（湊不到下限、也沒有其他料會再來時就開爐）。
    /// </summary>
    public sealed record Rates(
        Func<ProcessStep, double> StepSpeed, double HandoverSeconds, double VehicleMetersPerSecond, bool TestModeRules, double TruckSpeed);

    public enum LotPhase
    {
        /// <summary>圓棒不夠，在原料倉等料。</summary>
        WaitingForMaterial,

        /// <summary>在站上等車、或在車上，要送到 <see cref="LotInput.Step"/> 那一站。</summary>
        Moving,
        Queued,

        /// <summary>某台機台正在做（逐件工序）。</summary>
        Running,

        /// <summary>在某一爐裡（整爐工序）。</summary>
        InFurnace,

        /// <summary>已經包裝完（等裝箱或貨車）。</summary>
        Finished,
    }

    public sealed class LotInput
    {
        public required Guid Id { get; init; }
        public required string LotNumber { get; init; }
        public required string BatchNumber { get; init; }
        public required string PartCode { get; init; }
        public int RodDiameterMm { get; init; }
        public int Quantity { get; init; }
        public bool IsMakeup { get; init; }
        public int ReworkCount { get; init; }
        public Guid? SalesOrderId { get; init; }
        public string? SalesOrderNumber { get; init; }
        public SchedulePriority Priority { get; init; }

        /// <summary>目前所在或前往的工序（<see cref="MaterialLot.CurrentStepSequence"/>）。</summary>
        public int? Step { get; init; }
        public LotPhase Phase { get; init; }

        /// <summary>Queued：到站排隊的時間（從現在起幾秒，負數）。測試模式下爐前等待從最早到的那一框算起。</summary>
        public double QueuedAt { get; init; }

        /// <summary>Moving：從哪一站出發（在車上時是那一趟的起點）。</summary>
        public string? FromStation { get; init; }

        /// <summary>Moving：已經在車上，只剩路程與卸料。</summary>
        public bool OnVehicle { get; init; }

        /// <summary>Moving：要送到哪一站；null 表示 <see cref="Step"/> 那一站。</summary>
        public string? ToStation { get; init; }

        /// <summary>逐件工序的每件加工秒數覆寫（工單規格的粗加工、精加工），以站點代碼查；沒有的用製程設定。</summary>
        public IReadOnlyDictionary<string, double>? SecondsPerPiece { get; init; }

        /// <summary>切料站實際要切的支數（整根圓棒切完，最後一框連多出來的一起切）；不經過切料、或還不知道時是 null。</summary>
        public int? CutQuantity { get; init; }

        // Running：
        public string? MachineCode { get; init; }

        /// <summary>Running：實際開始的時間（從現在起幾秒，負數）。</summary>
        public double StartedAt { get; init; }
        public bool ChangedOver { get; init; }
        public int RemainingPieces { get; init; }

        /// <summary>Running：一件的週期（原本的秒數，取料＋加工＋取出），開始加工時就決定了。</summary>
        public double CycleSeconds { get; init; }

        /// <summary>
        /// Running：做完剩下的件數以外還要多少時間（原本的秒數）：還沒換完的線或刀、減掉目前這一件已經做的部分。
        /// </summary>
        public double LeadSeconds { get; init; }
    }

    public sealed class MachineInput
    {
        public required string Code { get; init; }
        public required string StationCode { get; init; }
        public int Number { get; init; }
        public MachineAssignment.Setup? Last { get; init; }

        /// <summary>刀具上次換刀後做了幾件（正在換刀或等刀時是 0：換完就歸零）。</summary>
        public int ToolPieces { get; init; }

        /// <summary>沒有在做的機台：還要停多久（原本的秒數，換刀或等刀）。</summary>
        public double StopSeconds { get; init; }

        /// <summary>沒有在做的機台：做完的那一框還在機邊等車搬走。</summary>
        public bool HoldingFinishedBin { get; init; }
    }

    public sealed class RunInput
    {
        public required string StationCode { get; init; }
        public double StartedAt { get; init; }

        /// <summary>這一爐還剩多少時間（原本的秒數）。</summary>
        public double RemainingSeconds { get; init; }
        public required IReadOnlyList<Guid> LotIds { get; init; }
    }

    public sealed class Input
    {
        public required IReadOnlyList<ProcessStep> Steps { get; init; }
        public required ProductionSettings Settings { get; init; }
        public required IReadOnlyList<MachineInput> Machines { get; init; }

        /// <summary>排隊中的框依到站順序。</summary>
        public required IReadOnlyList<LotInput> Lots { get; init; }
        public required IReadOnlyList<RunInput> Runs { get; init; }

        /// <summary>距離下一班貨車還有多久（原本的秒數）。</summary>
        public double TruckRemainingSeconds { get; init; }

        /// <summary>兩站交接點之間的路程（公尺）。</summary>
        public required Func<string, string, double> Distance { get; init; }

        /// <summary>每座爐上一爐出爐的時間（從現在起幾秒，負數）；還沒出過爐的站不在裡面。</summary>
        public IReadOnlyDictionary<string, double> LastRunCompletedAt { get; init; } = new Dictionary<string, double>();
    }

    public sealed record BinPlan(string MachineCode, LotInput Lot, int Quantity, double Start, double End, bool Running, bool Changeover);

    public sealed record RunPlan(string StationCode, IReadOnlyList<LotInput> Lots, int Pieces, double Start, double End, bool Running);

    public sealed class Result
    {
        public List<BinPlan> Bins { get; } = [];
        public List<RunPlan> Runs { get; } = [];

        /// <summary>每一框包裝完的時間（一開始就包完的是 0）。</summary>
        public Dictionary<Guid, double> FinishedAt { get; } = [];

        /// <summary>算到最後還停住的框與原因。</summary>
        public Dictionary<Guid, ForecastBlocker> Blocked { get; } = [];

        /// <summary>每座爐前湊不到一爐的框（站點代碼 → 框）。</summary>
        public Dictionary<string, List<LotInput>> Stranded { get; } = [];

        internal Func<double, double> NextTruck { get; set; } = t => t;

        internal Func<string, string, double> Hop { get; set; } = (_, _) => 0;

        /// <summary><paramref name="readyAt"/> 之後（含）第一班貨車到的時間。</summary>
        public double NextTruckAfter(double readyAt) => NextTruck(readyAt);

        /// <summary>搬運一趟（交接兩次加上路程）的估計時間。</summary>
        public double HopSeconds(string from, string to) => Hop(from, to);
    }

    private const int MaxEvents = 1_000_000;

    private readonly Input _input;
    private readonly Rates _rates;
    private readonly Result _result = new();
    private readonly PriorityQueue<Action, (double Time, long Seq)> _events = new();
    private readonly Dictionary<int, List<SimLot>> _queues = [];
    private readonly Dictionary<string, SimMachine> _machines;
    private readonly HashSet<string> _busyFurnaces = [];
    private readonly Dictionary<string, double> _furnaceFreeAt;
    private readonly Dictionary<string, double> _furnaceWake = [];
    private readonly List<SimLot> _lots;
    private readonly Dictionary<(string, string), double> _distances = [];
    private long _seq;
    private double _now;

    private ScheduleForecaster(Input input, Rates rates)
    {
        _input = input;
        _rates = rates;
        _machines = input.Machines.ToDictionary(m => m.Code, m => new SimMachine(m));
        _lots = input.Lots.Select(l => new SimLot(l)).ToList();
        _furnaceFreeAt = new Dictionary<string, double>(input.LastRunCompletedAt);
        _result.Hop = Hop;
        var first = input.TruckRemainingSeconds / rates.TruckSpeed;
        var interval = input.Settings.TruckIntervalSeconds / rates.TruckSpeed;
        _result.NextTruck = t => t <= first || interval <= 0 ? Math.Max(first, 0) : first + Math.Ceiling((t - first) / interval) * interval;
    }

    public static Result Run(Input input, Rates rates)
    {
        var forecaster = new ScheduleForecaster(input, rates);
        forecaster.Simulate();
        return forecaster._result;
    }

    private IReadOnlyList<ProcessStep> Steps => _input.Steps;

    private ProductionSettings Settings => _input.Settings;

    private void Simulate()
    {
        Initialize();
        Dispatch();
        var processed = 0;
        while (_events.TryPeek(out _, out var head))
        {
            _now = head.Time;
            // 同一個時間點的事件全部處理完才挑框，才不會因為事件的先後讓同時到站的框錯過優先順序。
            while (_events.TryPeek(out _, out var next) && next.Time <= _now)
            {
                _events.Dequeue()();
                if (++processed > MaxEvents)
                {
                    throw new InvalidOperationException("Schedule forecast did not settle.");
                }
            }

            Dispatch();
        }

        foreach (var lot in _lots.Where(l => l.Phase != LotPhase.Finished))
        {
            if (lot.Phase == LotPhase.WaitingForMaterial)
            {
                _result.Blocked[lot.In.Id] = ForecastBlocker.WaitingForMaterial;
                continue;
            }

            _result.Blocked[lot.In.Id] = ForecastBlocker.WaitingForFurnaceFill;
            if (lot.Phase == LotPhase.Queued && StepOf(lot.Step) is { } step)
            {
                if (!_result.Stranded.TryGetValue(step.StationCode, out var stranded))
                {
                    _result.Stranded[step.StationCode] = stranded = [];
                }

                stranded.Add(lot.In);
            }
        }
    }

    private void Initialize()
    {
        foreach (var machine in _machines.Values)
        {
            var speed = SpeedAt(machine.In.StationCode);
            var freeAt = Math.Max(machine.In.StopSeconds / speed, machine.In.HoldingFinishedBin ? _rates.HandoverSeconds : 0);
            if (freeAt > 0)
            {
                machine.Busy = true;
                At(freeAt, () => machine.Busy = false);
            }
        }

        foreach (var lot in _lots)
        {
            switch (lot.Phase)
            {
                case LotPhase.Finished:
                    _result.FinishedAt[lot.In.Id] = 0;
                    break;
                case LotPhase.Moving:
                    if (StepOf(lot.Step) is not { } target)
                    {
                        // 走完製程、送去暫存點的框（沒有訂單的批次）。
                        lot.Phase = LotPhase.Finished;
                        break;
                    }

                    var to = lot.In.ToStation ?? target.StationCode;
                    var from = lot.In.FromStation ?? to;
                    var travel = Travel(from, to) + _rates.HandoverSeconds;
                    At(lot.In.OnVehicle ? travel : travel + _rates.HandoverSeconds, () => Arrive(lot));
                    break;
                case LotPhase.Queued:
                    Enqueue(lot);
                    break;
                case LotPhase.Running:
                    StartRunning(lot);
                    break;
            }
        }

        foreach (var run in _input.Runs)
        {
            var step = Steps.Single(s => s.StationCode == run.StationCode);
            var lots = _lots.Where(l => run.LotIds.Contains(l.In.Id)).ToList();
            StartFurnace(step, lots, run.StartedAt, run.RemainingSeconds / _rates.StepSpeed(step), running: true);
        }
    }

    /// <summary>已經在做的那一框：照剩下的件數算完成時間，刀具到壽命一樣要換刀。</summary>
    private void StartRunning(SimLot lot)
    {
        var step = StepOf(lot.Step)!;
        var machine = _machines[lot.In.MachineCode!];
        var speed = _rates.StepSpeed(step);
        var (pieces, toolAfter) = Machining(step, machine, lot.In.RemainingPieces, lot.In.CycleSeconds);
        var end = Math.Max(0, (lot.In.LeadSeconds + pieces) / speed);
        machine.Last = SetupOf(lot.In);
        Occupy(machine, step, lot, lot.In.Quantity, lot.In.StartedAt, end, toolAfter / speed, running: true, lot.In.ChangedOver);
    }

    private void Dispatch()
    {
        foreach (var step in Steps)
        {
            if (!_queues.TryGetValue(step.Sequence, out var queue) || queue.Count == 0)
            {
                continue;
            }

            if (step.Kind == ProcessStepKind.Piece)
            {
                DispatchMachines(step, queue);
            }
            else if (!_busyFurnaces.Contains(step.StationCode))
            {
                DispatchFurnace(step, queue);
            }
        }
    }

    private void DispatchMachines(ProcessStep step, List<SimLot> queue)
    {
        var free = _machines.Values.Where(m => m.In.StationCode == step.StationCode && !m.Busy)
            .OrderBy(m => m.In.Number)
            .Select(m => new MachineAssignment.FreeMachine(m.In.Code, m.In.Number, m.Last))
            .ToList();
        if (free.Count == 0)
        {
            return;
        }

        var assigned = MachineAssignment.Assign(free, queue, l => SetupOf(l.In), step.UsesTool, l => l.In.Priority);
        foreach (var (picked, lot) in assigned)
        {
            queue.Remove(lot);
            var machine = _machines[picked.Code];
            var speed = _rates.StepSpeed(step);
            var quantity = step.IsCutting && lot.In.CutQuantity is { } cut ? cut : lot.In.Quantity;
            var perPiece = lot.In.SecondsPerPiece is { } custom && custom.TryGetValue(step.StationCode, out var seconds) && seconds > 0
                ? seconds
                : step.SecondsPerPiece;
            var cycle = perPiece + (step.IsManual ? 0 : 2 * Settings.PieceHandlingSeconds);
            var changeover = step.UsesTool && MachineAssignment.NeedsChangeover(machine.Last, lot.In.PartCode, lot.In.RodDiameterMm)
                ? Settings.ChangeoverSeconds
                : 0;
            var (pieces, toolAfter) = Machining(step, machine, quantity, cycle);
            machine.Last = SetupOf(lot.In);
            lot.Phase = LotPhase.Running;
            Occupy(machine, step, lot, quantity, _now, _now + (changeover + pieces) / speed, toolAfter / speed, running: false, changeover > 0);
        }
    }

    /// <summary>
    /// 做 <paramref name="pieces"/> 件要多久（原本的秒數），以及做完之後機台還要停多久換刀（最後一件剛好用完刀具時）。
    /// 會更新機台的刀具計數。
    /// </summary>
    private (double Seconds, double ToolAfter) Machining(ProcessStep step, SimMachine machine, int pieces, double cycle)
    {
        var seconds = pieces * cycle;
        if (!step.UsesTool || step.ToolLifePieces <= 0)
        {
            return (seconds, 0);
        }

        var after = 0d;
        for (var i = 1; i <= pieces; i++)
        {
            if (++machine.ToolPieces < step.ToolLifePieces)
            {
                continue;
            }

            machine.ToolPieces = 0;
            if (i < pieces)
            {
                seconds += Settings.ToolChangeSeconds;
            }
            else
            {
                after = Settings.ToolChangeSeconds;
            }
        }

        return (seconds, after);
    }

    /// <summary>
    /// 機台做這一框到 <paramref name="end"/>；之後還要換完刀（<paramref name="toolAfter"/>），
    /// 而且做完的框要等車取走（包裝站包好的框留在站上，不用等）才接下一框。
    /// </summary>
    private void Occupy(
        SimMachine machine, ProcessStep step, SimLot lot, int quantity, double start, double end, double toolAfter, bool running, bool changeover)
    {
        machine.Busy = true;
        _result.Bins.Add(new BinPlan(machine.In.Code, lot.In, quantity, start, end, running, changeover));
        At(end, () => Finish(lot, step, end));
        var pickup = step.IsPacking ? 0 : _rates.HandoverSeconds;
        At(end + Math.Max(toolAfter, pickup), () => machine.Busy = false);
    }

    /// <summary>
    /// 整爐工序開爐，規則同 <see cref="ProductionService"/>：排隊的支數達到下限才開；反工品另外累積；
    /// 湊不到下限時，補做的料（測試模式下任何料）在已經沒有其他料會再來時也開。急單、交期早的先裝，其餘照到站順序，裝到上限為止。
    /// </summary>
    private void DispatchFurnace(ProcessStep step, List<SimLot> arrived)
    {
        var arrivedIds = arrived.Select(l => l.In.Id).ToHashSet();
        var active = _lots.Where(l => l.Step is not null && l.Phase != LotPhase.Finished).ToList();
        var inspection = Steps.FirstOrDefault(s => s.IsInspection)?.Sequence ?? int.MaxValue;

        var rework = arrived.Where(l => l.In.ReworkCount > 0).ToList();
        var reworkIds = rework.Select(l => l.In.Id).ToHashSet();
        var reworkReleased = rework.Sum(l => l.In.Quantity) >= Settings.ReworkBatchMinPieces
            || !active.Any(l => l.Step <= inspection && !reworkIds.Contains(l.In.Id));
        var waitedOut = WaitedOut(step, arrived);
        var waiting = reworkReleased || waitedOut ? arrived : arrived.Except(rework).ToList();

        var moreComing = active.Any(l => l.Step <= step.Sequence && !arrivedIds.Contains(l.In.Id));
        var enough = waitedOut
            || waiting.Sum(l => l.In.Quantity) >= step.BatchMinPieces
            || (waiting.Count > 0 && !moreComing && (_rates.TestModeRules || waiting.Any(l => l.In.IsMakeup)));
        if (!enough)
        {
            return;
        }

        var load = new List<SimLot>();
        var used = 0d;
        foreach (var lot in waiting.OrderBy(l => l.In.Priority))
        {
            var share = (double)lot.In.Quantity / step.BatchCapacityFor(lot.In.RodDiameterMm);
            if (used + share <= 1 + 1e-9)
            {
                load.Add(lot);
                used += share;
            }
        }

        if (load.Count == 0)
        {
            return;
        }

        foreach (var lot in load)
        {
            arrived.Remove(lot);
        }

        StartFurnace(step, load, _now, _now + step.BatchSeconds / _rates.StepSpeed(step), running: false);
    }

    /// <summary>
    /// 測試模式的規則：爐空著、爐前有料等超過設定的秒數就直接開爐（使用者 2026-10-23）。
    /// 從最早到的那一框與上一爐出爐較晚的那一刻算起；還沒到時，在到的那一刻再挑一次。
    /// </summary>
    private bool WaitedOut(ProcessStep step, List<SimLot> arrived)
    {
        if (!_rates.TestModeRules || arrived.Count == 0)
        {
            return false;
        }

        var start = arrived.Min(l => l.ArrivedAt);
        if (_furnaceFreeAt.TryGetValue(step.StationCode, out var freeAt) && freeAt > start)
        {
            start = freeAt;
        }

        var due = start + Settings.TestFurnaceWaitSeconds;
        if (_now >= due - 1e-9)
        {
            return true;
        }

        if (!_furnaceWake.TryGetValue(step.StationCode, out var wake) || wake != due)
        {
            _furnaceWake[step.StationCode] = due;
            At(due, () => { });
        }

        return false;
    }

    private void StartFurnace(ProcessStep step, List<SimLot> lots, double start, double end, bool running)
    {
        end = Math.Max(end, 0);
        _busyFurnaces.Add(step.StationCode);
        foreach (var lot in lots)
        {
            lot.Phase = LotPhase.InFurnace;
        }

        _result.Runs.Add(new RunPlan(step.StationCode, lots.Select(l => l.In).ToList(), lots.Sum(l => l.In.Quantity), start, end, running));
        At(end, () =>
        {
            _busyFurnaces.Remove(step.StationCode);
            _furnaceFreeAt[step.StationCode] = end;
            foreach (var lot in lots)
            {
                Finish(lot, step, end);
            }
        });
    }

    /// <summary>一框做完這一道：包裝完就算完成；否則送往下一道（沒有下一道是送去暫存點，也算完成）。品管當作全部合格。</summary>
    private void Finish(SimLot lot, ProcessStep step, double at)
    {
        if (step.IsPacking)
        {
            lot.Step = null;
            lot.Phase = LotPhase.Finished;
            _result.FinishedAt[lot.In.Id] = at;
            return;
        }

        var next = Steps.FirstOrDefault(s => s.Sequence > step.Sequence);
        if (next is null)
        {
            lot.Step = null;
            lot.Phase = LotPhase.Finished;
            _result.FinishedAt[lot.In.Id] = at + Hop(step.StationCode, Settings.BufferStationCode);
            return;
        }

        lot.Step = next.Sequence;
        lot.Phase = LotPhase.Moving;
        At(at + Hop(step.StationCode, next.StationCode), () => Arrive(lot));
    }

    private void Arrive(SimLot lot)
    {
        lot.ArrivedAt = _now;
        Enqueue(lot);
    }

    private void Enqueue(SimLot lot)
    {
        lot.Phase = LotPhase.Queued;
        var step = lot.Step!.Value;
        if (!_queues.TryGetValue(step, out var queue))
        {
            _queues[step] = queue = [];
        }

        queue.Add(lot);
    }

    private void At(double time, Action action) => _events.Enqueue(action, (Math.Max(time, _now), _seq++));

    private ProcessStep? StepOf(int? sequence) => sequence is { } s ? Steps.FirstOrDefault(step => step.Sequence == s) : null;

    private double SpeedAt(string stationCode) =>
        Steps.FirstOrDefault(s => s.StationCode == stationCode) is { } step ? _rates.StepSpeed(step) : 1;

    /// <summary>搬運一趟：取料交接、路程、卸料交接。</summary>
    private double Hop(string from, string to) => 2 * _rates.HandoverSeconds + Travel(from, to);

    private double Travel(string from, string to)
    {
        if (from == to)
        {
            return 0;
        }

        if (!_distances.TryGetValue((from, to), out var meters))
        {
            _distances[(from, to)] = meters = _input.Distance(from, to);
        }

        return meters / _rates.VehicleMetersPerSecond;
    }

    private static MachineAssignment.Setup SetupOf(LotInput lot) => new(lot.BatchNumber, lot.PartCode, lot.RodDiameterMm);

    private sealed class SimLot(LotInput input)
    {
        public LotInput In { get; } = input;
        public int? Step { get; set; } = input.Step;
        public LotPhase Phase { get; set; } = input.Phase;

        /// <summary>到站排隊的時間；一開始就在排隊的框是實際到站的時間。</summary>
        public double ArrivedAt { get; set; } = input.QueuedAt;
    }

    private sealed class SimMachine(MachineInput input)
    {
        public MachineInput In { get; } = input;
        public MachineAssignment.Setup? Last { get; set; } = input.Last;
        public int ToolPieces { get; set; } = input.ToolPieces;
        public bool Busy { get; set; }
    }
}
