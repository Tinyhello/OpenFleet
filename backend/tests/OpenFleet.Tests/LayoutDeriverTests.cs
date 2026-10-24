using OpenFleet.Api.Data;
using OpenFleet.Api.Layout;
using OpenFleet.Contracts;
using OpenFleet.Layout;
using Xunit;

namespace OpenFleet.Tests;

/// <summary>
/// 佈局推導（不需要資料庫）：預設佈局推導出來的站點、機台、刀具、路網與車輛，要和第 1b 步啟動時寫入的資料相同；
/// 自訂佈局（加台數、加車位、加寬廠房）要推導正確。
/// </summary>
public class LayoutDeriverTests
{
    private static readonly ProcessStep[] Steps =
    [
        Step(0, "cutting", "切料", usesTool: false),
        Step(1, "rough", "粗加工", usesTool: true),
        Step(2, "fine", "精加工", usesTool: true),
        Step(3, "heat", "淬火", usesTool: false),
        Step(4, "temper", "回火", usesTool: false),
        Step(5, "plating", "電鍍", usesTool: false),
        Step(6, "polish", "拋光", usesTool: false),
        Step(7, "qc", "品管", usesTool: false),
        Step(8, "packing", "包裝", usesTool: false),
    ];

    private static ProcessStep Step(int sequence, string code, string name, bool usesTool) => new()
    {
        Sequence = sequence,
        Code = code,
        Name = name,
        UsesTool = usesTool,
        Kind = code is "heat" or "temper" or "plating" ? ProcessStepKind.Batch : ProcessStepKind.Piece,
    };

    private static void AddModule(FactoryLayoutDocument document, ModuleKind kind, string code, double x, double z, int rotation) =>
        document.Modules.Add(new LayoutModule { Kind = kind, Code = code, X = x, Z = z, Rotation = rotation });

    [Fact]
    public void Default_layout_stations_match_the_step_1b_seed()
    {
        var derived = LayoutDeriver.Derive(DefaultLayout.Build(), Steps);

        // 第 1b 步 DatabaseInitializer 寫進資料庫的站點：代碼、名稱、交接點、容量、工序。
        var expected = new (string Code, string Name, double X, double Z, int? Capacity, int? Sequence)[]
        {
            ("cutting-1", "切料", -28, -9.4, 30, 0),
            ("raw-store", "原料倉", -20, -9.4, null, null),
            ("rough-1", "粗加工 1 號機", -12, -9.4, 30, 1),
            ("rough-2", "粗加工 2 號機", -4, -9.4, 30, 1),
            ("fine-1", "精加工 1 號機", 4, -9.4, 30, 2),
            ("fine-2", "精加工 2 號機", 12, -9.4, 30, 2),
            ("heat-1", "淬火", 20, -9.4, null, 3),
            ("temper-1", "回火", 28, -9.4, null, 4),
            ("plating-1", "電鍍", 36, -9.4, null, 5),
            ("polish-1", "拋光", 20, 9.4, null, 6),
            ("qc-1", "品管", 4, 9.4, null, 7),
            ("packing-1", "包裝", -4, 9.4, null, 8),
            ("finished-store", "成品倉", -12, 9.4, null, null),
            ("shipping", "出貨", -20, 9.4, null, null),
            ("tool-store", "刀具庫", 0, -3.2, null, null),
        };

        Assert.Equal(expected.Length, derived.Stations.Count);
        foreach (var e in expected)
        {
            var station = derived.Stations.Single(s => s.Code == e.Code);
            Assert.Equal((e.Name, e.X, e.Z, e.Capacity, e.Sequence), (station.Name, station.HandoffX, station.HandoffZ, station.InboundCapacity, station.StepSequence));
        }
    }

    [Fact]
    public void Default_layout_machines_and_tools_match_the_step_1b_seed()
    {
        var derived = LayoutDeriver.Derive(DefaultLayout.Build(), Steps);

        // 每個製程站就是一台機台（代碼等於站代碼、編號 1、名稱是站名）；倉儲站與裝飾物不是機台。
        Assert.Equal(8, derived.Machines.Count);
        Assert.All(derived.Machines, m => Assert.Equal((m.Code, 1), (m.StationCode, m.Number)));
        Assert.Equal("粗加工 2 號機", derived.Machines.Single(m => m.Code == "rough-2").Name);

        // 有刀具的工序（粗加工、精加工）每台機台各一筆刀具狀態。
        Assert.Equal(new[] { "fine-1", "fine-2", "rough-1", "rough-2" }, derived.ToolMachineCodes.Order());
    }

    [Fact]
    public void Default_layout_vehicles_are_the_five_bays_with_their_homes()
    {
        var derived = LayoutDeriver.Derive(DefaultLayout.Build(), Steps);

        var vehicles = derived.Vehicles
            .OrderBy(v => v.Id, StringComparer.Ordinal)
            .Select(v => (v.Id, v.Type, v.HomeX, v.HomeZ, v.InLayout, v.DispatchEnabled))
            .ToList();
        Assert.Equal(
            new (string, VehicleType, double?, double?, bool, bool)[]
            {
                ("agv-01", VehicleType.Large, -10.3, 0.2, true, true),
                ("agv-02", VehicleType.Large, -8, 0.2, true, true),
                ("agv-03", VehicleType.Small, -5.7, 0.2, true, true),
                ("agv-04", VehicleType.Large, -12.6, 0.2, true, true),
                ("agv-05", VehicleType.Large, -14.4, 0.2, true, true),
            },
            vehicles);
    }

    [Fact]
    public void Default_layout_road_network_and_handoffs_match_the_step_1b_roads()
    {
        var derived = LayoutDeriver.Derive(DefaultLayout.Build(), Steps);

        Assert.Equal(100, derived.RouteNodes.Count);
        Assert.Equal(132, derived.RouteEdges.Count);

        // 每個交接點都落在路網節點上（驗證規則的前提）。
        var codes = derived.RouteNodes.Select(n => n.Code).ToHashSet(StringComparer.Ordinal);
        Assert.All(derived.Stations, s => Assert.Contains(DefaultLayout.Code(s.HandoffX, s.HandoffZ), codes));
    }

    [Fact]
    public void Custom_layout_with_more_machines_bays_and_a_wider_floor_derives_correctly()
    {
        // 3 台粗加工、2 座淬火爐、6 台大型 AGV＋1 台小型，廠房往東加寬。
        var document = DefaultLayout.Build();
        document.Floor.MaxX = 60;
        AddModule(document, ModuleKind.RoughLathe, "rough-3", 44, -12.8, 0);
        AddModule(document, ModuleKind.QuenchFurnace, "heat-2", 52, -12.8, 0);
        AddModule(document, ModuleKind.AgvBayLarge, "agv-06", -16, 0, 0);
        AddModule(document, ModuleKind.AgvBayLarge, "agv-07", -18, 0, 0);

        var derived = LayoutDeriver.Derive(document, Steps);

        // 同一道工序有好幾台：站名加號數（第 4 節的站名規則）。
        Assert.Equal(
            new[] { "粗加工 1 號機", "粗加工 2 號機", "粗加工 3 號機" },
            derived.Stations.Where(s => s.StepSequence == 1).OrderBy(s => s.Code, StringComparer.Ordinal).Select(s => s.Name));
        Assert.Equal(
            new[] { "淬火 1 號爐", "淬火 2 號爐" },
            derived.Stations.Where(s => s.StepSequence == 3).OrderBy(s => s.Code, StringComparer.Ordinal).Select(s => s.Name));
        Assert.Contains("rough-3", derived.ToolMachineCodes);
        Assert.Equal(9, derived.Machines.Count);

        Assert.Equal(6, derived.Vehicles.Count(v => v.Type == VehicleType.Large));
        Assert.Equal(1, derived.Vehicles.Count(v => v.Type == VehicleType.Small));
    }
}
