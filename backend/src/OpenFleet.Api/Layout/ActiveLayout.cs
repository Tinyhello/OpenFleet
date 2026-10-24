using OpenFleet.Contracts;

namespace OpenFleet.Api.Layout;

/// <summary>
/// 目前使用中的佈局（Singleton）：版本、每站交接點對應的路網節點、每台車的車位與車型。
/// 套用時整份換掉（不可變的快照，和 <see cref="Traffic.RoadNetwork"/> 相同），所以讀的人永遠看到同一個版本。
/// </summary>
public sealed class ActiveLayout
{
    private volatile Snapshot _current = Snapshot.Empty;
    private long _version;

    /// <summary>每換一次佈局就加一；發布端據此知道要重新發布佈局版本。</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>目前的佈局版本與車輛清單；還沒載入時是 null。</summary>
    public LayoutRevisionMessage? Revision => _current.Revision;

    /// <summary>不在佈局裡的車（發布端用來清掉它們在 Broker 上的舊狀態）。</summary>
    public IReadOnlyCollection<string> RetiredVehicleIds => _current.RetiredVehicleIds;

    public bool TryStationNode(string stationCode, out string nodeCode)
    {
        if (_current.StationNodes.TryGetValue(stationCode, out var found))
        {
            nodeCode = found;
            return true;
        }

        nodeCode = string.Empty;
        return false;
    }

    /// <summary>車輛車位對應的路網節點；不在佈局裡的車回傳 null。</summary>
    public string? HomeNodeOf(string vehicleId) => _current.HomeNodes.GetValueOrDefault(vehicleId);

    /// <summary>車型；不在佈局裡的車回傳 Unknown（不會被派工）。</summary>
    public VehicleType TypeOf(string vehicleId) => _current.Types.GetValueOrDefault(vehicleId, VehicleType.Unknown);

    public void Load(
        LayoutRevisionMessage revision,
        IReadOnlyDictionary<string, string> stationNodes,
        IReadOnlyDictionary<string, string> homeNodes,
        IReadOnlyCollection<string> retiredVehicleIds)
    {
        var types = revision.Vehicles.ToDictionary(v => v.VehicleId, v => v.Type, StringComparer.Ordinal);
        _current = new Snapshot(revision, stationNodes, homeNodes, types, retiredVehicleIds);
        Interlocked.Increment(ref _version);
    }

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, VehicleType>(StringComparer.Ordinal),
            []);

        public Snapshot(
            LayoutRevisionMessage? revision,
            IReadOnlyDictionary<string, string> stationNodes,
            IReadOnlyDictionary<string, string> homeNodes,
            IReadOnlyDictionary<string, VehicleType> types,
            IReadOnlyCollection<string> retiredVehicleIds)
        {
            Revision = revision;
            StationNodes = stationNodes;
            HomeNodes = homeNodes;
            Types = types;
            RetiredVehicleIds = retiredVehicleIds;
        }

        public LayoutRevisionMessage? Revision { get; }
        public IReadOnlyDictionary<string, string> StationNodes { get; }
        public IReadOnlyDictionary<string, string> HomeNodes { get; }
        public IReadOnlyDictionary<string, VehicleType> Types { get; }
        public IReadOnlyCollection<string> RetiredVehicleIds { get; }
    }
}
