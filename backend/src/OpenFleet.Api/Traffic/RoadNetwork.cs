namespace OpenFleet.Api.Traffic;

public sealed record RoadNode(string Code, double X, double Z);

/// <summary>
/// 路網（Singleton）：節點與不分方向的路段。車道不限方向，所以路段是無向的；同一時間誰可以用哪一段由
/// <see cref="TrafficController"/> 的預約決定。
/// 佈局套用時整份換掉（P7 第 2 步）：內部是不可變的 <see cref="Graph"/>，<see cref="Load"/> 建好新的再一次換掉參考；
/// 每個公開方法只取一次參考，所以規劃路徑時不會看到半新半舊的路網。
/// </summary>
public sealed class RoadNetwork
{
    private volatile Graph _graph = Graph.Empty;

    public RoadNetwork()
    {
    }

    public RoadNetwork(IEnumerable<RoadNode> nodes, IEnumerable<(string From, string To)> edges)
    {
        Load(nodes, edges);
    }

    public IReadOnlyCollection<RoadNode> Nodes => _graph.Nodes;

    public void Load(IEnumerable<RoadNode> nodes, IEnumerable<(string From, string To)> edges)
    {
        _graph = Graph.Build(nodes, edges);
    }

    public RoadNode Node(string code) => _graph.Node(code);

    public IReadOnlyList<(string To, double Length)> Neighbours(string code) => _graph.Neighbours(code);

    /// <summary>只有一個出入口的節點（站點交接點、充電車位）：進去之後只能原路出來。</summary>
    public bool IsDeadEnd(string code) => _graph.Neighbours(code).Count == 1;

    /// <summary>無向路段的識別：兩端代碼排序後組成，A→B 與 B→A 是同一段。</summary>
    public static (string, string) EdgeKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>離某個座標最近的節點；車輛剛出現或後端重啟時用來判斷它在路網上的位置。</summary>
    public RoadNode Nearest(double x, double z) => _graph.Nearest(x, z);

    /// <summary>
    /// 最短路徑（Dijkstra），回傳含起點與終點的節點代碼；到不了時回傳 null。
    /// <paramref name="blockedNodes"/>／<paramref name="blockedEdges"/> 是這次規劃要避開的資源（例如被別台車占用的）。
    /// </summary>
    public IReadOnlyList<string>? FindPath(
        string from,
        string to,
        IReadOnlySet<string>? blockedNodes = null,
        IReadOnlySet<(string, string)>? blockedEdges = null) =>
        _graph.FindPath(from, to, blockedNodes, blockedEdges);

    /// <summary>沿路網的最短距離（公尺）；到不了時回傳無限大。</summary>
    public double PathLength(string from, string to) => _graph.PathLength(from, to);

    /// <summary>一份不再改變的路網。</summary>
    private sealed class Graph
    {
        public static readonly Graph Empty = new(
            new Dictionary<string, RoadNode>(StringComparer.Ordinal),
            new Dictionary<string, List<(string To, double Length)>>(StringComparer.Ordinal));

        private readonly Dictionary<string, RoadNode> _nodes;
        private readonly Dictionary<string, List<(string To, double Length)>> _neighbours;

        private Graph(Dictionary<string, RoadNode> nodes, Dictionary<string, List<(string To, double Length)>> neighbours)
        {
            _nodes = nodes;
            _neighbours = neighbours;
        }

        public IReadOnlyCollection<RoadNode> Nodes => _nodes.Values;

        public static Graph Build(IEnumerable<RoadNode> nodes, IEnumerable<(string From, string To)> edges)
        {
            var byCode = new Dictionary<string, RoadNode>(StringComparer.Ordinal);
            var neighbours = new Dictionary<string, List<(string To, double Length)>>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                byCode.Add(node.Code, node);
                neighbours.Add(node.Code, []);
            }

            foreach (var (from, to) in edges)
            {
                var length = Distance(byCode[from], byCode[to]);
                neighbours[from].Add((to, length));
                neighbours[to].Add((from, length));
            }

            return new Graph(byCode, neighbours);
        }

        public RoadNode Node(string code) => _nodes[code];

        public IReadOnlyList<(string To, double Length)> Neighbours(string code) => _neighbours[code];

        public RoadNode Nearest(double x, double z) =>
            _nodes.Values.MinBy(n => (n.X - x) * (n.X - x) + (n.Z - z) * (n.Z - z))
            ?? throw new InvalidOperationException("Road network is empty.");

        public IReadOnlyList<string>? FindPath(
            string from,
            string to,
            IReadOnlySet<string>? blockedNodes,
            IReadOnlySet<(string, string)>? blockedEdges)
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

        public double PathLength(string from, string to)
        {
            var path = FindPath(from, to, null, null);
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
}
