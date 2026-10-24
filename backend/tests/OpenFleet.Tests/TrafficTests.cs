using Microsoft.Extensions.Time.Testing;
using OpenFleet.Api.Traffic;
using OpenFleet.Contracts;

namespace OpenFleet.Tests;

public class RoadNetworkTests
{
    private static readonly RoadNetwork Network = Build();

    private static RoadNetwork Build()
    {
        var (nodes, edges) = DemoRoadNetwork.Build();
        return new RoadNetwork(nodes, edges);
    }

    [Fact]
    public void Demo_network_is_connected_and_has_one_dead_end_per_station_and_parking_slot()
    {
        var start = Network.Nodes.First().Code;

        Assert.All(Network.Nodes, node => Assert.NotNull(Network.FindPath(start, node.Code)));

        // 9 個下排站點（切料、原料倉、粗加工 1～2、精加工 1～2、淬火、回火、電鍍）+ 5 個上排站點 + 刀具庫（島區，接在內圈車道上）+ 5 個充電車位。
        Assert.Equal(20, Network.Nodes.Count(n => Network.IsDeadEnd(n.Code)));
    }

    [Fact]
    public void Nodes_are_far_enough_apart_that_one_vehicle_per_node_cannot_overlap()
    {
        // 大型車長 1.6 m。
        var nodes = Network.Nodes.ToList();
        var closest = nodes.SelectMany(a => nodes.Where(b => b != a).Select(b => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2)))).Min();

        Assert.True(closest >= 1.6 - 1e-9, $"Closest pair of nodes is only {closest:0.00} m apart.");
    }

    [Fact]
    public void Shortest_path_can_use_either_lane_and_avoids_blocked_nodes()
    {
        // 原料倉交接點到粗加工 1 號機交接點：出來、沿外圈走 4 + 4 m、進去。
        var direct = Network.FindPath("-20,-9.4", "-12,-9.4")!;
        Assert.Equal(new[] { "-20,-9.4", "-20,-7", "-16,-7", "-12,-7", "-12,-9.4" }, direct);
        Assert.Equal(2.4 + 4 + 4 + 2.4, Network.PathLength("-20,-9.4", "-12,-9.4"), 6);

        // 外圈中間被占用：改走內圈繞過去。
        var detour = Network.FindPath("-20,-9.4", "-12,-9.4", blockedNodes: new HashSet<string> { "-16,-7" })!;
        Assert.Contains("-16,-5.2", detour);
        Assert.DoesNotContain("-16,-7", detour);

        // 目的地本身被占用：沒有路。
        Assert.Null(Network.FindPath("-20,-9.4", "-12,-9.4", blockedNodes: new HashSet<string> { "-12,-9.4" }));
    }
}

/// <summary>
/// 交通控制的行為。用一個極簡的車輛模型驅動：每一輪把各車的位置與進度交給控制器，套用它回傳的放行，
/// 再讓每台車沿已放行的路前進一個節點。每一輪都檢查沒有兩台車在同一個節點上。
/// </summary>
public class TrafficControllerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero));
    private readonly RoadNetwork _network;
    private readonly TrafficController _traffic;
    private readonly List<Car> _cars = [];

    public TrafficControllerTests()
    {
        var (nodes, edges) = DemoRoadNetwork.Build();
        _network = new RoadNetwork(nodes, edges);
        _traffic = new TrafficController(_network, new TrafficOptions(), _time);
    }

    [Fact]
    public void Vehicle_is_cleared_a_few_nodes_ahead_and_frees_the_road_behind_it()
    {
        var car = Add("agv-01", "-10.3,0.2", goal: "-20,-9.4");

        Tick();

        // 前瞻 2 個節點：出發點加上前方 2 個。
        Assert.Equal(3, car.Released);
        Assert.Equal(3, _traffic.ReservedNodes().Count);

        RunUntil(() => car.Node == "-20,-9.4");
        Tick();

        // 回報到達之後，只占著終點。
        Assert.Equal(new[] { "-20,-9.4" }, _traffic.ReservedNodes().Keys);
        Assert.True(_traffic.IsAt("agv-01", "-20,-9.4"));
    }

    [Fact]
    public void Vehicle_without_permission_to_move_gets_its_route_but_no_road()
    {
        // 上料中：路徑已經規劃好（畫面可以預告），但還沒放行。
        var car = Add("agv-01", "-20,-9.4", goal: "-12,-9.4", canMove: false);

        Tick();
        Tick();

        Assert.Equal((5, 1, "-20,-9.4"), (car.Path.Count, car.Released, car.Node));
        Assert.True(_traffic.IsAt("agv-01", "-20,-9.4"));

        car.CanMove = true;
        RunUntil(() => car.Node == "-12,-9.4");
    }

    [Fact]
    public void Vehicle_blocked_by_a_parked_one_goes_around_through_the_other_lane()
    {
        // 一台沒有任務的車停在外圈車道上，剛好在另一台的最短路徑上。
        var parked = Add("agv-01", "-16,-7", goal: null);
        var car = Add("agv-02", "-20,-9.4", goal: "-12,-9.4");

        RunUntil(() => car.Node == "-12,-9.4");

        Assert.Equal("-16,-7", parked.Node);
        Assert.Contains("-16,-5.2", car.Visited);
    }

    [Fact]
    public void Head_on_vehicles_in_the_same_lane_resolve_the_deadlock_and_both_arrive()
    {
        // 兩台車在外圈下邊相向而行，最短路徑互相穿過對方。
        var east = Add("agv-01", "-16,-7", goal: "20,-9.4");
        var west = Add("agv-02", "12,-7", goal: "-12,-9.4");

        RunUntil(() => east.Node == "20,-9.4" && west.Node == "-12,-9.4");
    }

    [Fact]
    public void Vehicle_heading_for_an_occupied_handoff_waits_before_the_junction()
    {
        // 精加工的交接點上有車在作業（沒有要離開）。
        var busy = Add("agv-01", "4,-9.4", goal: null);
        var car = Add("agv-02", "-20,-9.4", goal: "4,-9.4");

        Run(seconds: 30);

        // 等在路口之前，路口 (-4,-7) 保持暢通，交接點上的車之後才出得來。
        Assert.NotEqual("4,-7", car.Node);
        Assert.NotEqual("4,-9.4", car.Node);
        Assert.DoesNotContain("4,-7", car.Visited);

        busy.Goal = "-10.3,0.2";
        RunUntil(() => car.Node == "4,-9.4" && busy.Node == "-10.3,0.2");
    }

    [Fact]
    public void Vehicle_stuck_at_a_junction_yields_so_the_one_in_the_spur_can_leave()
    {
        // 最糟的情況：一台已經停在路口要進支線，支線裡的車要出來。誰也沒有別的路，只能有一台先退開。
        var leaving = Add("agv-01", "4,-9.4", goal: "20,-9.4");
        var entering = Add("agv-02", "4,-7", goal: "4,-9.4");

        RunUntil(() => leaving.Node == "20,-9.4" && entering.Node == "4,-9.4");
    }

    [Fact]
    public void Vehicles_parked_on_each_others_junction_swap_places()
    {
        // 兩台車各自停在對方要進的充電車位入口，都沒有別的路：一台要先退開，而且退開後不能馬上把原地占回來。
        var left = Add("agv-02", "-10.3,5.2", goal: "-8,0.2");
        var right = Add("agv-03", "-8,5.2", goal: "-10.3,0.2");

        RunUntil(() => left.Node == "-8,0.2" && right.Node == "-10.3,0.2");
    }

    [Fact]
    public void Offline_vehicle_keeps_its_node_and_a_restarted_one_is_placed_on_the_nearest_node()
    {
        var car = Add("agv-01", "-10.3,0.2", goal: "-20,-9.4");
        Run(seconds: 2);
        var heldBefore = _traffic.ReservedNodes().Count;

        // 離線：實體上還占著原地，預約不能放掉，也不會再往前要路。
        car.Online = false;
        Run(seconds: 5);
        Assert.Contains(car.Node, _traffic.ReservedNodes().Keys);
        Assert.True(_traffic.ReservedNodes().Count <= heldBefore);

        // 設備重啟（新的遙測 Session），出現在別的位置：舊的預約全部釋放，從最近的節點重新開始。
        car.Online = true;
        car.Restart(_network.Node("12,7"));
        car.Goal = null;
        Tick();
        Tick();
        Assert.Equal(new[] { "12,7" }, _traffic.ReservedNodes().Keys);
    }

    [Fact]
    public void Three_vehicles_with_changing_destinations_never_share_a_node_and_always_arrive()
    {
        // 壓力測試：三台車不斷被派往隨機的站點與車位（亂數種子固定，結果可重現）。
        var random = new Random(20261009);
        var destinations = _network.Nodes.Where(n => _network.IsDeadEnd(n.Code)).Select(n => n.Code).ToList();
        var cars = new[] { Add("agv-01", "-10.3,0.2", null), Add("agv-02", "-8,0.2", null), Add("agv-03", "-5.7,0.2", null) };
        var arrivals = 0;

        for (var round = 0; round < 40; round++)
        {
            // 每台車的目的地都不同，也不是別台車現在停的地方。
            var taken = cars.Select(c => c.Node).ToHashSet();
            foreach (var car in cars)
            {
                var free = destinations.Where(d => !taken.Contains(d)).ToList();
                car.Goal = free[random.Next(free.Count)];
                taken.Add(car.Goal);
            }

            RunUntil(() => cars.All(c => c.Node == c.Goal), maxSeconds: 600);
            arrivals += cars.Length;
        }

        Assert.Equal(120, arrivals);
    }

    private Car Add(string id, string node, string? goal, bool canMove = true)
    {
        var car = new Car(id, _network.Node(node)) { Goal = goal, CanMove = canMove };
        _cars.Add(car);
        return car;
    }

    private void Run(int seconds)
    {
        for (var i = 0; i < seconds * 5; i++)
        {
            Tick();
        }
    }

    private void RunUntil(Func<bool> done, int maxSeconds = 300)
    {
        for (var i = 0; i < maxSeconds * 5; i++)
        {
            if (done())
            {
                return;
            }

            Tick();
        }

        Assert.Fail("Vehicles did not arrive: " + string.Join("; ", _cars.Select(c => $"{c.Id} at {c.Node} -> {c.Goal}")));
    }

    /// <summary>一輪 0.2 秒：控制器放行 → 車輛套用 → 每台車前進一個已放行的節點。</summary>
    private void Tick()
    {
        var inputs = _cars.Select(c => new TrafficInput(c.Id, c.Online, c.Session, c.X, c.Z, c.PlanId, c.Revision, c.Reached, c.Goal, c.CanMove)).ToList();
        foreach (var (vehicleId, clearance) in _traffic.Update(inputs))
        {
            _cars.Single(c => c.Id == vehicleId).Apply(clearance, _network);
        }

        foreach (var car in _cars.Where(c => c.Online))
        {
            car.Step(_network);
        }

        // 不變條件：任何時候都沒有兩台車在同一個節點上。
        var shared = _cars.GroupBy(c => c.Node).FirstOrDefault(g => g.Count() > 1);
        Assert.True(shared is null, $"Vehicles {string.Join(", ", shared?.Select(c => c.Id) ?? [])} share node {shared?.Key}.");

        _time.Advance(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>測試用的車：和模擬器一樣只照放行走，但一輪直接跳一個節點。</summary>
    private sealed class Car(string id, RoadNode start)
    {
        public string Id { get; } = id;
        public Guid Session { get; private set; } = Guid.NewGuid();
        public bool Online { get; set; } = true;
        public string? Goal { get; set; }
        public bool CanMove { get; set; } = true;
        public double X { get; private set; } = start.X;
        public double Z { get; private set; } = start.Z;
        public string Node { get; private set; } = start.Code;
        public Guid? PlanId { get; private set; }
        public long Revision { get; private set; }
        public List<string> Path { get; private set; } = [];
        public int Released { get; private set; }
        public int Reached { get; private set; }
        public List<string> Visited { get; } = [start.Code];

        public void Apply(VehicleClearance clearance, RoadNetwork network)
        {
            if (clearance.PlanId == PlanId)
            {
                if (clearance.Revision <= Revision)
                {
                    return;
                }
            }
            else
            {
                PlanId = clearance.PlanId;
                Reached = 0;
            }

            Revision = clearance.Revision;
            Path = clearance.Path.Select(p => network.Nearest(p.X, p.Z).Code).ToList();
            Released = clearance.ReleasedCount;
        }

        public void Step(RoadNetwork network)
        {
            if (Reached >= Released)
            {
                return;
            }

            var node = network.Node(Path[Reached]);
            Reached++;
            if (node.Code != Node)
            {
                (X, Z, Node) = (node.X, node.Z, node.Code);
                Visited.Add(node.Code);
            }
        }

        public void Restart(RoadNode position)
        {
            Session = Guid.NewGuid();
            PlanId = null;
            Revision = 0;
            Path = [];
            Released = 0;
            Reached = 0;
            (X, Z, Node) = (position.X, position.Z, position.Code);
        }
    }
}
