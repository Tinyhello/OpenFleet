using Microsoft.Extensions.Options;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Simulator;

/// <summary>
/// 模擬器內所有車輛的狀態（模擬器是自己車輛的唯一狀態來源）。
/// 每次程序啟動產生新的 SessionId，後端據此辨認「設備重啟」。
/// 車輛平常靜止；收到 Transport／MoveTo 才沿走道移動，並把過程中的回報放進待發送佇列。
/// </summary>
public sealed class SimulatedFleet
{
    private const int RepliesToRemember = 1_000;
    private const double ArrivalTolerance = 1e-9;

    private readonly Lock _gate = new();
    private readonly SimulatorSettings _settings;
    private readonly string _producerId;
    private readonly TimeProvider _time;
    private readonly LoopRoute _route;
    private readonly Dictionary<string, SimulatedVehicle> _vehicles;
    private readonly Dictionary<Guid, List<MessageEnvelope<VehicleEvent>>> _replies = [];
    private readonly Queue<Guid> _replyOrder = new();
    private readonly Queue<OutgoingEvent> _outgoing = new();

    public SimulatedFleet(IOptions<SimulatorSettings> settings, IOptions<MqttSettings> mqtt, TimeProvider time)
    {
        _settings = settings.Value;
        _producerId = mqtt.Value.ProducerId;
        _time = time;
        _route = new LoopRoute(_settings.Route);

        // 起始位置沿走道平均錯開，避免重疊。
        var spacing = _route.Length / _settings.VehicleIds.Count;
        _vehicles = _settings.VehicleIds
            .Select((id, index) => new SimulatedVehicle(id, _route.PointAt(index * spacing)))
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
                }));
            }

            return result;
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

                case VehicleCommandType.MoveTo when command.Destination is null:
                    Emit(source, VehicleEventType.Rejected, "MoveTo needs destination");
                    break;

                case VehicleCommandType.Transport or VehicleCommandType.MoveTo when vehicle.Job is { IsTransport: true }:
                    // 搬運中不接受其他移動指令：物料還在車上或正要去取。
                    Emit(source, VehicleEventType.Rejected, "Busy");
                    break;

                case VehicleCommandType.Transport:
                    Supersede(vehicle);
                    vehicle.Job = Job.Transport(source, command.Destination!, command.LoadQuantity, command.LoadCode,
                        command.HandoverSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : _settings.HandoverDuration);
                    vehicle.Job.Waypoints = Plan(vehicle, command.Pickup!);
                    Emit(source, VehicleEventType.Accepted);
                    break;

                case VehicleCommandType.MoveTo:
                    Supersede(vehicle);
                    vehicle.Job = Job.Move(source);
                    vehicle.Job.Waypoints = Plan(vehicle, command.Destination!);
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
        if (vehicle.Job is not { } job)
        {
            return false;
        }

        if (job.Phase is JobPhase.Loading or JobPhase.Unloading)
        {
            job.HandoverRemaining -= elapsed;
            if (job.HandoverRemaining > TimeSpan.Zero)
            {
                return false;
            }

            if (job.Phase == JobPhase.Loading)
            {
                // 交接完成才回報 Loaded；抵達取料點本身不代表已取料。物料從這一刻起算在車上，車速也跟著變慢。
                vehicle.LoadQuantity = job.LoadQuantity;
                vehicle.LoadCode = job.LoadCode;
                Emit(job.Source, VehicleEventType.Loaded);
                job.Phase = JobPhase.ToDestination;
                job.Waypoints = Plan(vehicle, job.Destination!);
            }
            else
            {
                vehicle.LoadQuantity = 0;
                vehicle.LoadCode = null;
                Emit(job.Source, VehicleEventType.Completed);
                vehicle.Job = null;
            }

            return false;
        }

        // 容許極小的浮點誤差：累加多步之後差 1e-15 也算到達，直接對齊到目標點。
        var budget = speed * elapsed.TotalSeconds + ArrivalTolerance;
        while (budget > ArrivalTolerance && job.Waypoints.TryPeek(out var target))
        {
            var dx = target.X - vehicle.X;
            var dz = target.Z - vehicle.Z;
            var distance = Math.Sqrt(dx * dx + dz * dz);
            if (distance <= budget)
            {
                (vehicle.X, vehicle.Z) = target;
                budget -= distance;
                job.Waypoints.Dequeue();
            }
            else
            {
                vehicle.X += dx / distance * budget;
                vehicle.Z += dz / distance * budget;
                budget = 0;
            }
        }

        if (job.Waypoints.Count > 0)
        {
            return true;
        }

        switch (job.Phase)
        {
            case JobPhase.ToPickup:
                job.Phase = JobPhase.Loading;
                job.HandoverRemaining = job.HandoverDuration;
                break;
            case JobPhase.ToDestination when job.IsTransport:
                job.Phase = JobPhase.Unloading;
                job.HandoverRemaining = job.HandoverDuration;
                break;
            default:
                Emit(job.Source, VehicleEventType.Completed);
                vehicle.Job = null;
                break;
        }

        return true;
    }

    /// <summary>新的指令取代進行中的 MoveTo（例如回充電區途中被派工）。</summary>
    private void Supersede(SimulatedVehicle vehicle)
    {
        if (vehicle.Job is { IsTransport: false } moving)
        {
            Emit(moving.Source, VehicleEventType.Failed, "Superseded");
            vehicle.Job = null;
        }
    }

    private Queue<(double X, double Z)> Plan(SimulatedVehicle vehicle, Position3 target) =>
        new(_route.PlanPath(vehicle.X, vehicle.Z, target.X, target.Z));

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

    private sealed class Job
    {
        private Job(CommandSource source, bool isTransport, JobPhase phase, Position3? destination)
        {
            Source = source;
            IsTransport = isTransport;
            Phase = phase;
            Destination = destination;
        }

        public int LoadQuantity { get; private init; }
        public string? LoadCode { get; private init; }

        /// <summary>取料、卸料各需要的交接時間。</summary>
        public TimeSpan HandoverDuration { get; private init; }

        /// <summary>目前這次交接的進度，0～1。</summary>
        public double HandoverProgress => HandoverDuration <= TimeSpan.Zero
            ? 1
            : Math.Clamp(1 - HandoverRemaining / HandoverDuration, 0, 1);

        public CommandSource Source { get; }
        public bool IsTransport { get; }
        public JobPhase Phase { get; set; }

        /// <summary>Transport 的卸料點；取料完成後才規劃過去的路徑。</summary>
        public Position3? Destination { get; }
        public Queue<(double X, double Z)> Waypoints { get; set; } = new();
        public TimeSpan HandoverRemaining { get; set; }

        public static Job Transport(CommandSource source, Position3 destination, int loadQuantity, string? loadCode, TimeSpan handoverDuration) =>
            new(source, isTransport: true, JobPhase.ToPickup, destination)
            {
                LoadQuantity = loadQuantity,
                LoadCode = loadCode,
                HandoverDuration = handoverDuration,
            };

        public static Job Move(CommandSource source) =>
            new(source, isTransport: false, JobPhase.ToDestination, destination: null);
    }

    private sealed class SimulatedVehicle(string vehicleId, (double X, double Z) position)
    {
        public string VehicleId { get; } = vehicleId;
        public double X { get; set; } = position.X;
        public double Z { get; set; } = position.Z;
        public long Sequence { get; set; }
        public Job? Job { get; set; }

        /// <summary>車上的件數；取料交接完成後才有值，卸料交接完成後歸零。</summary>
        public int LoadQuantity { get; set; }
        public string? LoadCode { get; set; }
    }
}
