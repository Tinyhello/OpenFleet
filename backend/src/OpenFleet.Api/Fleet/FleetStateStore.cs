using Microsoft.Extensions.Options;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Fleet;

public enum TelemetryOutcome
{
    Applied,

    /// <summary>同一 Session 的序號沒有前進（重複或亂序），或來自已被取代的舊 Session。</summary>
    Ignored,
}

/// <summary>
/// P1 的車輛狀態存放（記憶體內，Singleton）。P2 起改由資料庫保存業務狀態。
/// 所有變更都在同一把鎖內完成，並在變更後把「複本」放進 <see cref="StateChangeQueue"/>，
/// 避免外部拿到內部物件參考後在鎖外修改。
/// </summary>
public sealed class FleetStateStore(TimeProvider time, StateChangeQueue changes, IOptions<FleetOptions> options)
{
    private const int RetiredSessionsToRemember = 8;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, VehicleRecord> _vehicles = new(StringComparer.Ordinal);

    /// <summary>
    /// 本次啟動的狀態世代。Version 只存在記憶體，重啟後會從 0 重新計算；
    /// 顯示端看到不同的 Epoch 就知道要整個取代，而不是拿 Version 比大小。
    /// </summary>
    public Guid StateEpoch { get; } = Guid.NewGuid();

    public TelemetryOutcome ApplyTelemetry(string vehicleId, VehicleTelemetry telemetry)
    {
        lock (_gate)
        {
            var record = GetOrAdd(vehicleId);
            var state = record.State;

            if (record.RetiredSessions.Contains(telemetry.SessionId))
            {
                return TelemetryOutcome.Ignored;
            }

            if (state.TelemetrySessionId == telemetry.SessionId)
            {
                if (telemetry.Sequence <= state.TelemetrySequence)
                {
                    return TelemetryOutcome.Ignored;
                }
            }
            else if (state.TelemetrySessionId != Guid.Empty)
            {
                // 設備重啟產生新 Session：舊 Session 之後遲到的封包一律丟棄。
                record.RetireSession(state.TelemetrySessionId, RetiredSessionsToRemember);
            }

            state.TelemetrySessionId = telemetry.SessionId;
            state.TelemetrySequence = telemetry.Sequence;
            state.Position = Copy(telemetry.Position);
            state.Speed = telemetry.Speed;
            state.Activity = telemetry.Activity;
            state.ActivityProgress = telemetry.ActivityProgress;
            state.LoadQuantity = telemetry.LoadQuantity;
            state.LoadCode = telemetry.LoadCode;
            MarkSeen(state);
            Publish(state);
            return TelemetryOutcome.Applied;
        }
    }

    public void ApplyEvent(string vehicleId, VehicleEvent vehicleEvent)
    {
        lock (_gate)
        {
            var state = GetOrAdd(vehicleId).State;
            state.LastCommandId = vehicleEvent.CommandId;
            state.LastCommandEvent = vehicleEvent.EventType;
            MarkSeen(state);
            Publish(state);
        }
    }

    /// <summary>把超過 <paramref name="offlineAfter"/> 沒有回報的車輛標記為 Offline，回傳被改變的數量。</summary>
    public int MarkStaleVehiclesOffline(TimeSpan offlineAfter)
    {
        var now = time.GetUtcNow();
        var changed = 0;

        lock (_gate)
        {
            foreach (var record in _vehicles.Values)
            {
                var state = record.State;
                if (state.Connectivity == VehicleConnectivity.Online && now - state.LastSeenAt > offlineAfter)
                {
                    state.Connectivity = VehicleConnectivity.Offline;
                    state.Version++;
                    Publish(state);
                    changed++;
                }
            }
        }

        return changed;
    }

    public bool Contains(string vehicleId)
    {
        lock (_gate)
        {
            return _vehicles.ContainsKey(vehicleId);
        }
    }

    public VehicleState? Find(string vehicleId)
    {
        lock (_gate)
        {
            return _vehicles.TryGetValue(vehicleId, out var record) ? Copy(record.State) : null;
        }
    }

    public IReadOnlyList<VehicleState> Snapshot()
    {
        lock (_gate)
        {
            return _vehicles.Values
                .Select(r => Copy(r.State))
                .OrderBy(s => s.VehicleId, StringComparer.Ordinal)
                .ToList();
        }
    }

    private VehicleRecord GetOrAdd(string vehicleId)
    {
        if (!_vehicles.TryGetValue(vehicleId, out var record))
        {
            var type = options.Value.VehicleTypes.GetValueOrDefault(vehicleId, VehicleType.Unknown);
            record = new VehicleRecord(new VehicleState { VehicleId = vehicleId, VehicleType = type, Connectivity = VehicleConnectivity.Offline, StateEpoch = StateEpoch });
            _vehicles.Add(vehicleId, record);
        }

        return record;
    }

    private void MarkSeen(VehicleState state)
    {
        state.LastSeenAt = time.GetUtcNow();
        state.Connectivity = VehicleConnectivity.Online;
        state.Version++;
    }

    private void Publish(VehicleState state) => changes.Enqueue(Copy(state));

    private static Position3 Copy(Position3 p) => new() { X = p.X, Y = p.Y, Z = p.Z };

    private static VehicleState Copy(VehicleState s) => new()
    {
        VehicleId = s.VehicleId,
        VehicleType = s.VehicleType,
        Connectivity = s.Connectivity,
        Position = Copy(s.Position),
        Speed = s.Speed,
        Activity = s.Activity,
        ActivityProgress = s.ActivityProgress,
        LoadQuantity = s.LoadQuantity,
        LoadCode = s.LoadCode,
        LastSeenAt = s.LastSeenAt,
        TelemetrySessionId = s.TelemetrySessionId,
        TelemetrySequence = s.TelemetrySequence,
        LastCommandId = s.LastCommandId,
        LastCommandEvent = s.LastCommandEvent,
        StateEpoch = s.StateEpoch,
        Version = s.Version,
    };

    private sealed class VehicleRecord(VehicleState state)
    {
        private readonly Queue<Guid> _retiredOrder = new();

        public VehicleState State { get; } = state;

        public HashSet<Guid> RetiredSessions { get; } = [];

        public void RetireSession(Guid sessionId, int capacity)
        {
            if (!RetiredSessions.Add(sessionId))
            {
                return;
            }

            _retiredOrder.Enqueue(sessionId);
            if (_retiredOrder.Count > capacity)
            {
                RetiredSessions.Remove(_retiredOrder.Dequeue());
            }
        }
    }
}
