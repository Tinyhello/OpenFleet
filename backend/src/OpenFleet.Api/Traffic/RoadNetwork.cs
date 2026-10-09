namespace OpenFleet.Api.Traffic;

public sealed record RoadNode(string Code, double X, double Z);

/// <summary>
/// 路網（Singleton，啟動時從資料庫載入後不再改變）：節點與不分方向的路段。
/// 車道不限方向，所以路段是無向的；同一時間誰可以用哪一段由 <see cref="TrafficController"/> 的預約決定。
/// </summary>
public sealed class RoadNetwork
{
    private readonly Dictionary<string, RoadNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string To, double Length)>> _neighbours = new(StringComparer.Ordinal);

    public RoadNetwork()
    {
    }

    public RoadNetwork(IEnumerable<RoadNode> nodes, IEnumerable<(string From, string To)> edges)
    {
        Load(nodes, edges);
    }

    public IReadOnlyCollection<RoadNode> Nodes => _nodes.Values;

    public void Load(IEnumerable<RoadNode> nodes, IEnumerable<(string From, string To)> edges)
    {
        _nodes.Clear();
        _neighbours.Clear();
        foreach (var node in nodes)
        {
            _nodes.Add(node.Code, node);
            _neighbours.Add(node.Code, []);
        }

        foreach (var (from, to) in edges)
        {
            var length = Distance(_nodes[from], _nodes[to]);
            _neighbours[from].Add((to, length));
            _neighbours[to].Add((from, length));
        }
    }

    public RoadNode Node(string code) => _nodes[code];

    public IReadOnlyList<(string To, double Length)> Neighbours(string code) => _neighbours[code];

    /// <summary>只有一個出入口的節點（站點交接點、充電車位）：進去之後只能原路出來。</summary>
    public bool IsDeadEnd(string code) => _neighbours[code].Count == 1;

    /// <summary>無向路段的識別：兩端代碼排序後組成，A→B 與 B→A 是同一段。</summary>
    public static (string, string) EdgeKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>離某個座標最近的節點；車輛剛出現或後端重啟時用來判斷它在路網上的位置。</summary>
    public RoadNode Nearest(double x, double z) =>
        _nodes.Values.MinBy(n => (n.X - x) * (n.X - x) + (n.Z - z) * (n.Z - z))
        ?? throw new InvalidOperationException("Road network is empty.");

    /// <summary>
    /// 最短路徑（Dijkstra），回傳含起點與終點的節點代碼；到不了時回傳 null。
    /// <paramref name="blockedNodes"/>／<paramref name="blockedEdges"/> 是這次規劃要避開的資源（例如被別台車占用的）。
    /// </summary>
    public IReadOnlyList<string>? FindPath(
        string from,
        string to,
        IReadOnlySet<string>? blockedNodes = null,
        IReadOnlySet<(string, string)>? blockedEdges = null)
    {
        if (from == to)
        {
            return [from];
        }

        var distance = new Dictionary<string, double>(StringComparer.Ordinal) { [from] = 0 };
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new PriorityQueue<string, double>();
        queue.Enqueue(from, 0);

        while (queue.TryDequeue(out var current, out var travelled))
        {
            if (travelled > distance[current])
            {
                continue;
            }

            if (current == to)
            {
                break;
            }

            foreach (var (next, length) in _neighbours[current])
            {
                if ((blockedNodes?.Contains(next) ?? false) || (blockedEdges?.Contains(EdgeKey(current, next)) ?? false))
                {
                    continue;
                }

                var candidate = travelled + length;
                if (!distance.TryGetValue(next, out var known) || candidate < known - 1e-9)
                {
                    distance[next] = candidate;
                    previous[next] = current;
                    queue.Enqueue(next, candidate);
                }
            }
        }

        if (!previous.ContainsKey(to))
        {
            return null;
        }

        var path = new List<string> { to };
        while (path[^1] != from)
        {
            path.Add(previous[path[^1]]);
        }

        path.Reverse();
        return path;
    }

    /// <summary>沿路網的最短距離（公尺）；到不了時回傳無限大。</summary>
    public double PathLength(string from, string to)
    {
        var path = FindPath(from, to);
        if (path is null)
        {
            return double.PositiveInfinity;
        }

        var total = 0d;
        for (var i = 1; i < path.Count; i++)
        {
            total += Distance(_nodes[path[i - 1]], _nodes[path[i]]);
        }

        return total;
    }

    private static double Distance(RoadNode a, RoadNode b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
}

/// <summary>
/// 示範路網（暫定佈局）：環狀走道有內外兩條車道，車道之間在各站的位置有連接路段；
/// 站點交接點與充電車位是只有一個出入口的支線。啟動時寫入資料庫（RouteNodes／RouteEdges）。
/// 節點間距不小於車長，所以「一個節點只給一台車」就足以保證車輛不會重疊。
/// </summary>
public static class DemoRoadNetwork
{
    private const double OuterZ = 6, OuterX = 18, InnerZ = 4.2, InnerX = 16.2, HandoffZ = 7.6, ParkingZ = 0.2;

    private static readonly double[] StationColumns = [-16, -8, 0, 8, 16];
    private static readonly double[] ParkingColumns = [-10.3, -8, -5.7];

    public static string Code(double x, double z) => FormattableString.Invariant($"{x:0.##},{z:0.##}");

    public static (List<RoadNode> Nodes, List<(string From, string To)> Edges) Build()
    {
        var nodes = new Dictionary<string, RoadNode>(StringComparer.Ordinal);
        var edges = new HashSet<(string, string)>();

        string Add(double x, double z)
        {
            var code = Code(x, z);
            nodes.TryAdd(code, new RoadNode(code, x, z));
            return code;
        }

        void Link(string a, string b) => edges.Add(RoadNetwork.EdgeKey(a, b));

        // 把一圈節點依序相連，最後接回第一個。
        void Ring(List<string> ring)
        {
            for (var i = 0; i < ring.Count; i++)
            {
                Link(ring[i], ring[(i + 1) % ring.Count]);
            }
        }

        // 外圈車道：下邊與上邊每 4 m 一個節點，兩側各兩個。
        double[] outerXs = [-16, -12, -8, -4, 0, 4, 8, 12, 16];
        var outer = new List<string> { Add(-OuterX, -OuterZ) };
        outer.AddRange(outerXs.Select(x => Add(x, -OuterZ)));
        outer.AddRange([Add(OuterX, -OuterZ), Add(OuterX, -2), Add(OuterX, 2), Add(OuterX, OuterZ)]);
        outer.AddRange(outerXs.Reverse().Select(x => Add(x, OuterZ)));
        outer.AddRange([Add(-OuterX, OuterZ), Add(-OuterX, 2), Add(-OuterX, -2)]);
        Ring(outer);

        // 內圈車道：轉角兼作最外側兩站的連接點；上邊多了三個充電車位的出入口。
        double[] innerBottomXs = [-12, -8, -4, 0, 4, 8, 12];
        double[] innerTopXs = [12, 8, 4, 0, -5.7, -8, -10.3];
        var inner = new List<string> { Add(-InnerX, -InnerZ) };
        inner.AddRange(innerBottomXs.Select(x => Add(x, -InnerZ)));
        inner.AddRange([Add(InnerX, -InnerZ), Add(InnerX, 0), Add(InnerX, InnerZ)]);
        inner.AddRange(innerTopXs.Select(x => Add(x, InnerZ)));
        inner.AddRange([Add(-InnerX, InnerZ), Add(-InnerX, 0)]);
        Ring(inner);

        // 內外車道之間的連接路段。
        foreach (var side in new[] { -1d, 1d })
        {
            foreach (var x in new double[] { -12, -8, -4, 0, 4, 8, 12 })
            {
                if (nodes.ContainsKey(Code(x, side * InnerZ)))
                {
                    Link(Code(x, side * OuterZ), Code(x, side * InnerZ));
                }
            }

            Link(Code(-16, side * OuterZ), Code(-InnerX, side * InnerZ));
            Link(Code(16, side * OuterZ), Code(InnerX, side * InnerZ));
        }

        // 站點交接點：接在外圈車道上的支線。
        foreach (var side in new[] { -1d, 1d })
        {
            foreach (var x in StationColumns)
            {
                Link(Add(x, side * HandoffZ), Code(x, side * OuterZ));
            }
        }

        // 充電車位：接在內圈車道上邊的支線。
        foreach (var x in ParkingColumns)
        {
            Link(Add(x, ParkingZ), Code(x, InnerZ));
        }

        return (nodes.Values.ToList(), edges.ToList());
    }
}
