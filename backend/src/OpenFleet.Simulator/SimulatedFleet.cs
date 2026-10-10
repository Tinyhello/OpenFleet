using Microsoft.Extensions.Options;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Simulator;

/// <summary>
/// 模擬器內所有車輛的狀態（模擬器是自己車輛的唯一狀態來源）。
/// 每次程序啟動產生新的 SessionId，後端據此辨認「設備重啟」。
///
/// 車輛自己不規劃路線、也不知道路網：路徑與「可以開到哪裡」都由後端的放行（<see cref="VehicleClearance"/>）決定，
/// 車只會沿著放行的路徑開到已放行的最後一點然後停下。搬運指令只決定「到了取料點、卸料點要做什麼」。
/// </summary>
public sealed class SimulatedFleet
{
    private const int RepliesToRemember = 1_000;
    private const double ArrivalTolerance = 1e-9;

    /// <summary>離交接點多近算是到了（公尺）。</summary>
    private const double HandoffTolerance = 0.05;

    private readonly Lock _gate = new();
    private readonly SimulatorSettings _settings;
    private readonly string _producerId;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, SimulatedVehicle> _vehicles;
    private readonly Dictionary<Guid, List<MessageEnvelope<VehicleEvent>>> _replies = [];
    private readonly Queue<Guid> _replyOrder = new();
    private readonly Queue<OutgoingEvent> _outgoing = new();

    public SimulatedFleet(IOptions<SimulatorSettings> settings, IOptions<MqttSettings> mqtt, TimeProvider time)
    {
        _settings = settings.Value;
        _producerId = mqtt.Value.ProducerId;
        _time = time;

        // 起始位置由設定指定（示範設定是充電車位）；沒指定的車排在原點旁邊，後端會把它們引到最近的節點。
        _vehicles = _settings.VehicleIds
            .Select((id, index) => _settings.StartPositions.TryGetValue(id, out var start)
                ? new SimulatedVehicle(id, start.X, start.Z)
                : new SimulatedVehicle(id, index * 2, 0))
            .ToDictionary(v => v.VehicleId, StringComparer.Ordinal);
    }

    public Guid SessionId { get; } = Guid.NewGuid();

    public bool Contains(string vehicleId) => _vehicles.ContainsKey(vehicleId);

    /// <summary>推進模擬時間並產生每台車的遙測。斷線時也持續推進：車子不會因為網路斷掉而停下。</summary>
    public IReadOnlyList<(string VehicleId, VehicleTelemetry Telemetry)> Advance(TimeSpan elapsed)
    {
        lock (_gate)
        {
            var result = new List<(string, VehicleTelemetry)>(_vehicles.Count);
            foreach (var vehicle in _vehicles.Values)
            {
                var speed = SpeedFor(vehicle);
                var moving = Step(vehicle, elapsed, speed);
                vehicle.Sequence++;

                var job = vehicle.Job;
                result.Add((vehicle.VehicleId, new VehicleTelemetry
                {
                    SessionId = SessionId,
                    Sequence = vehicle.Sequence,
                    Speed = moving ? speed : 0,
                    Position = new Position3 { X = vehicle.X, Y = 0, Z = vehicle.Z },
                    Activity = job?.Phase switch
                    {
                        JobPhase.Loading => VehicleActivity.Loading,
                        JobPhase.Unloading => VehicleActivity.Unloading,
                        _ => moving ? VehicleActivity.Moving : VehicleActivity.Idle,
                    },
                    ActivityProgress = job is { Phase: JobPhase.Loading or JobPhase.Unloading } ? job.HandoverProgress : 0,
                    LoadQuantity = vehicle.LoadQuantity,
                    LoadCode = vehicle.LoadQuantity > 0 ? vehicle.LoadCode : null,
                    RoutePlanId = vehicle.PlanId,
                    RouteRevision = vehicle.Revision,
                    RouteReachedCount = vehicle.Reached,
                    Route = vehicle.Path.Skip(vehicle.Reached).Select(Copy).ToList(),
                    RouteReleasedCount = Math.Max(0, vehicle.Released - vehicle.Reached),
                    ActiveCommandId = job?.Source.CommandId,
                }));
            }

            return result;
        }
    }

    /// <summary>
    /// 套用後端的放行。同一條路徑（PlanId 相同）只接受版本較新的內容，重送或亂序的舊訊息會被忽略；
    /// 已經走過的進度保留，因為後端保證已放行的部分不會改。換了一條路徑（PlanId 不同）就從頭開始走。
    /// </summary>
    public void ApplyClearance(string vehicleId, VehicleClearance clearance)
    {
        lock (_gate)
        {
            var vehicle = _vehicles[vehicleId];
            if (clearance.PlanId == vehicle.PlanId)
            {
                if (clearance.Revision <= vehicle.Revision)
                {
                    return;
                }
            }
            else
            {
                vehicle.PlanId = clearance.PlanId;
                vehicle.Reached = 0;
            }

            vehicle.Revision = clearance.Revision;
            vehicle.Path = clearance.Path.Select(Copy).ToList();
            vehicle.Released = Math.Clamp(clearance.ReleasedCount, vehicle.Reached, vehicle.Path.Count);
        }
    }

    /// <summary>
    /// 處理一道指令。設備端冪等：同一個 CommandId 只執行一次，重送時把已經產生過的回報（相同 MessageId）再送一次，
    /// 後端會以 MessageId 去重，因此不會產生第二次業務效果。回傳是否為重送。
    /// </summary>
    public bool HandleCommand(string vehicleId, MessageEnvelope<VehicleCommand> envelope)
    {
        lock (_gate)
        {
            var command = envelope.Payload;
            if (_replies.TryGetValue(command.CommandId, out var previous))
            {
                foreach (var reply in previous)
                {
                    _outgoing.Enqueue(new OutgoingEvent(vehicleId, reply));
                }

                return true;
            }

            var vehicle = _vehicles[vehicleId];
            var source = new CommandSource(vehicleId, command.CommandId, command.AssignmentId, envelope.MessageId);

            if (_time.GetUtcNow() > command.ExpiresAt)
            {
                Emit(source, VehicleEventType.Rejected, "Expired");
                return false;
            }

            switch (command.CommandType)
            {
                case VehicleCommandType.Ping:
                    Emit(source, VehicleEventType.Accepted);
                    Emit(source, VehicleEventType.Completed);
                    break;

                case VehicleCommandType.Transport when command.Pickup is null || command.Destination is null || command.AssignmentId is null:
                    Emit(source, VehicleEventType.Rejected, "Transport needs assignmentId, pickup and destination");
                    break;

                case VehicleCommandType.Transport when vehicle.Job is not null:
                    // 搬運中不接受另一筆搬運：物料還在車上或正要去取。
                    Emit(source, VehicleEventType.Rejected, "Busy");
                    break;

                case VehicleCommandType.Transport:
                    // 接受後不會自己出發：等後端放行到取料點的路。
                    vehicle.Job = new Job(source, command.Pickup!, command.Destination!, command.LoadQuantity, command.LoadCode,
                        command.HandoverSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : _settings.HandoverDuration);
                    Emit(source, VehicleEventType.Accepted);
                    break;

                default:
                    Emit(source, VehicleEventType.Rejected, $"Unsupported command type {command.CommandType}");
                    break;
            }

            return false;
        }
    }

    /// <summary>
    /// 待發送的回報。發送成功後才呼叫 <see cref="CompleteEvent"/> 移除；
    /// 斷線時留在佇列，重新連線後依原本順序補送（「完成」這類回報不能因為斷線而消失）。
    /// </summary>
    public bool TryPeekEvent(out string vehicleId, out MessageEnvelope<VehicleEvent> envelope)
    {
        lock (_gate)
        {
            if (_outgoing.TryPeek(out var next))
            {
                (vehicleId, envelope) = next;
                return true;
            }

            vehicleId = string.Empty;
            envelope = null!;
            return false;
        }
    }

    public void CompleteEvent()
    {
        lock (_gate)
        {
            _outgoing.TryDequeue(out _);
        }
    }

    /// <summary>
    /// 目前的車速：空車最快，載貨越多越慢，滿載時是 FullLoadSpeed。
    /// </summary>
    private double SpeedFor(SimulatedVehicle vehicle)
    {
        var loadRatio = Math.Clamp((double)vehicle.LoadQuantity / _settings.LoadCapacity, 0, 1);
        return _settings.Speed + (_settings.FullLoadSpeed - _settings.Speed) * loadRatio;
    }

    /// <summary>推進一台車，回傳這一步是否在移動。</summary>
    private bool Step(SimulatedVehicle vehicle, TimeSpan elapsed, double speed)
    {
        if (vehicle.Job is { Phase: JobPhase.Loading or JobPhase.Unloading } handover)
        {
            // 交接中不移動，但仍要確認「已經在新路徑的起點上」，回報的進度才正確。
            Drive(vehicle, 0);
            handover.HandoverRemaining -= elapsed;
            if (handover.HandoverRemaining > TimeSpan.Zero)
            {
                return false;
            }

            if (handover.Phase == JobPhase.Loading)
            {
                // 交接完成才回報 Loaded；抵達取料點本身不代表已取料。物料從這一刻起算在車上，車速也跟著變慢。
                vehicle.LoadQuantity = handover.LoadQuantity;
                vehicle.LoadCode = handover.LoadCode;
                Emit(handover.Source, VehicleEventType.Loaded);
                handover.Phase = JobPhase.ToDestination;
            }
            else
            {
                vehicle.LoadQuantity = 0;
                vehicle.LoadCode = null;
                Emit(handover.Source, VehicleEventType.Completed);
                vehicle.Job = null;
            }

            return false;
        }

        var moving = Drive(vehicle, speed * elapsed.TotalSeconds);

        // 停在交接點上才開始交接。交接點是支線的盡頭，不會只是路過。
        if (vehicle.Job is { } job)
        {
            if (job.Phase == JobPhase.ToPickup && IsAt(vehicle, job.Pickup))
            {
                job.Phase = JobPhase.Loading;
                job.HandoverRemaining = job.HandoverDuration;
            }
            else if (job.Phase == JobPhase.ToDestination && IsAt(vehicle, job.Destination))
            {
                job.Phase = JobPhase.Unloading;
                job.HandoverRemaining = job.HandoverDuration;
            }
        }

        return moving;
    }

    /// <summary>沿放行的路徑前進，最多走 <paramref name="distance"/> 公尺，而且不超過已放行的最後一點。</summary>
    private static bool Drive(SimulatedVehicle vehicle, double distance)
    {
        var budget = distance;
        var moved = false;

        while (vehicle.Reached < vehicle.Released)
        {
            var target = vehicle.Path[vehicle.Reached];
            var dx = target.X - vehicle.X;
            var dz = target.Z - vehicle.Z;
            var remaining = Math.Sqrt(dx * dx + dz * dz);
            moved |= remaining > ArrivalTolerance;

            // 容許極小的浮點誤差：累加多步之後差 1e-15 也算到達，直接對齊到目標點。
            // 誤差只用來判斷「到了沒」，不加進可以走的距離，否則每一步都會多走一點。
            if (remaining <= budget + ArrivalTolerance)
            {
                (vehicle.X, vehicle.Z) = (target.X, target.Z);
                budget = Math.Max(0, budget - remaining);
                vehicle.Reached++;
            }
            else
            {
                // 這一步走不到下一點：用完剩下的距離（可能是 0）就停。
                vehicle.X += dx / remaining * budget;
                vehicle.Z += dz / remaining * budget;
                return moved && budget > 0;
            }
        }

        return moved;
    }

    private static bool IsAt(SimulatedVehicle vehicle, Position3 point) =>
        Math.Abs(vehicle.X - point.X) <= HandoffTolerance && Math.Abs(vehicle.Z - point.Z) <= HandoffTolerance;

    private static Position3 Copy(Position3 p) => new() { X = p.X, Y = p.Y, Z = p.Z };

    private void Emit(CommandSource source, VehicleEventType type, string? reason = null)
    {
        var envelope = MessageEnvelope.Create(_producerId, new VehicleEvent
        {
            CommandId = source.CommandId,
            AssignmentId = source.AssignmentId,
            EventType = type,
            Reason = reason,
        }, _time.GetUtcNow(), correlationId: source.MessageId);

        if (!_replies.TryGetValue(source.CommandId, out var replies))
        {
            replies = [];
            _replies.Add(source.CommandId, replies);
            _replyOrder.Enqueue(source.CommandId);
            if (_replyOrder.Count > RepliesToRemember)
            {
                _replies.Remove(_replyOrder.Dequeue());
            }
        }

        replies.Add(envelope);
        _outgoing.Enqueue(new OutgoingEvent(source.VehicleId, envelope));
    }

    private readonly record struct OutgoingEvent(string VehicleId, MessageEnvelope<VehicleEvent> Envelope);

    /// <summary>回報時需要帶回的指令資訊。</summary>
    private sealed record CommandSource(string VehicleId, Guid CommandId, Guid? AssignmentId, Guid MessageId);

    private enum JobPhase
    {
        ToPickup,
        Loading,
        ToDestination,
        Unloading,
    }

    /// <summary>一筆搬運：到取料點交接、再到卸料點交接。怎麼開過去由放行決定。</summary>
    private sealed class Job(CommandSource source, Position3 pickup, Position3 destination, int loadQuantity, string? loadCode, TimeSpan handoverDuration)
    {
        public CommandSource Source { get; } = source;
        public Position3 Pickup { get; } = pickup;
        public Position3 Destination { get; } = destination;
        public int LoadQuantity { get; } = loadQuantity;
        public string? LoadCode { get; } = loadCode;

        /// <summary>取料、卸料各需要的交接時間。</summary>
        public TimeSpan HandoverDuration { get; } = handoverDuration;
        public JobPhase Phase { get; set; } = JobPhase.ToPickup;
        public TimeSpan HandoverRemaining { get; set; }

        /// <summary>目前這次交接的進度，0～1。</summary>
        public double HandoverProgress => HandoverDuration <= TimeSpan.Zero
            ? 1
            : Math.Clamp(1 - HandoverRemaining / HandoverDuration, 0, 1);
    }

    private sealed class SimulatedVehicle(string vehicleId, double x, double z)
    {
        public string VehicleId { get; } = vehicleId;
        public double X { get; set; } = x;
        public double Z { get; set; } = z;
        public long Sequence { get; set; }
        public Job? Job { get; set; }

        /// <summary>車上的件數；取料交接完成後才有值，卸料交接完成後歸零。</summary>
        public int LoadQuantity { get; set; }
        public string? LoadCode { get; set; }

        // 後端放行的路徑與進度。
        public Guid? PlanId { get; set; }
        public long Revision { get; set; }
        public List<Position3> Path { get; set; } = [];

        /// <summary>可以開到 Path[Released - 1]。</summary>
        public int Released { get; set; }

        /// <summary>已經到達的點數；下一個要去的是 Path[Reached]。</summary>
        public int Reached { get; set; }
    }
}
