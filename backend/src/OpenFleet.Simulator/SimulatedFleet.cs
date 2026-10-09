using Microsoft.Extensions.Options;
using OpenFleet.Contracts;

namespace OpenFleet.Simulator;

/// <summary>
/// 模擬器內所有車輛的狀態（模擬器是自己車輛的唯一狀態來源）。
/// 每次程序啟動產生新的 SessionId，後端據此辨認「設備重啟」。
/// </summary>
public sealed class SimulatedFleet
{
    private const int RepliesToRemember = 1_000;

    private readonly Lock _gate = new();
    private readonly SimulatorSettings _settings;
    private readonly LoopRoute _route;
    private readonly Dictionary<string, SimulatedVehicle> _vehicles;
    private readonly Dictionary<Guid, IReadOnlyList<MessageEnvelope<VehicleEvent>>> _replies = [];
    private readonly Queue<Guid> _replyOrder = new();

    public SimulatedFleet(IOptions<SimulatorSettings> settings)
    {
        _settings = settings.Value;
        _route = new LoopRoute(_settings.Route);

        // 車輛沿路線平均錯開，避免重疊（沒有避碰，同速行駛所以不會追撞）。
        var spacing = _route.Length / _settings.VehicleIds.Count;
        _vehicles = _settings.VehicleIds
            .Select((id, index) => new SimulatedVehicle(id, index * spacing))
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
                vehicle.Distance = (vehicle.Distance + _settings.Speed * elapsed.TotalSeconds) % _route.Length;
                vehicle.Sequence++;
                var (x, z) = _route.PointAt(vehicle.Distance);

                result.Add((vehicle.VehicleId, new VehicleTelemetry
                {
                    SessionId = SessionId,
                    Sequence = vehicle.Sequence,
                    Speed = _settings.Speed,
                    Position = new Position3 { X = x, Y = 0, Z = z },
                }));
            }

            return result;
        }
    }

    /// <summary>
    /// 設備端冪等：同一個 CommandId 只執行一次。重送時回傳第一次產生的回報（相同 MessageId），
    /// 後端會以 MessageId 去重，因此不會產生第二次業務效果。
    /// </summary>
    public IReadOnlyList<MessageEnvelope<VehicleEvent>> GetOrCreateReplies(
        Guid commandId,
        Func<IReadOnlyList<MessageEnvelope<VehicleEvent>>> execute,
        out bool isDuplicate)
    {
        lock (_gate)
        {
            if (_replies.TryGetValue(commandId, out var existing))
            {
                isDuplicate = true;
                return existing;
            }

            var replies = execute();
            _replies.Add(commandId, replies);
            _replyOrder.Enqueue(commandId);
            if (_replyOrder.Count > RepliesToRemember)
            {
                _replies.Remove(_replyOrder.Dequeue());
            }

            isDuplicate = false;
            return replies;
        }
    }

    private sealed class SimulatedVehicle(string vehicleId, double distance)
    {
        public string VehicleId { get; } = vehicleId;

        /// <summary>沿路線從起點算起的距離（公尺）。</summary>
        public double Distance { get; set; } = distance;
        public long Sequence { get; set; }
    }
}
