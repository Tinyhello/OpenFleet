using System;
using System.Linq;
using OpenFleet.Contracts;
using OpenFleet.Layout;
using Xunit;

namespace OpenFleet.Tests;

/// <summary>
/// 佈局的純計算（不需要資料庫）：幾何、預設佈局、驗證規則與站名。
/// 驗證規則的測試都從預設佈局改一處，所以每條規則都有「會錯」的佈局，預設佈局本身則是「不會錯」的對照。
/// </summary>
public class LayoutTests
{
    private static LayoutValidationDto Mutate(Action<FactoryLayoutDocument> change)
    {
        var document = DefaultLayout.Build();
        change(document);
        return LayoutValidator.Validate(document);
    }

    private static LayoutIssueDto Issue(LayoutValidationDto result, string code) =>
        result.Issues.SingleOrDefault(i => i.Code == code)
        ?? throw new Xunit.Sdk.XunitException($"No issue with code {code}; got: {string.Join(", ", result.Issues.Select(i => i.Code))}");

    private static LayoutModule Module(FactoryLayoutDocument document, string code) => document.Modules.Single(m => m.Code == code);

    private static void AddNode(FactoryLayoutDocument document, string code, double x, double z) =>
        document.RoadNodes.Add(new LayoutRoadNode { Code = code, X = x, Z = z });

    private static void AddEdge(FactoryLayoutDocument document, string from, string to) =>
        document.RoadEdges.Add(new LayoutRoadEdge { From = from, To = to });

    private static void NearPoint((double X, double Z) expected, (double X, double Z) actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
    }

    private static void NearRect(LayoutRect expected, LayoutRect actual)
    {
        Assert.Equal(expected.MinX, actual.MinX, 6);
        Assert.Equal(expected.MaxX, actual.MaxX, 6);
        Assert.Equal(expected.MinZ, actual.MinZ, 6);
        Assert.Equal(expected.MaxZ, actual.MaxZ, 6);
    }

    [Fact]
    public void Rotation_is_clockwise_seen_from_above_like_unity_yaw()
    {
        NearPoint((3.4, 0), LayoutGeometry.Rotate(0, 3.4, 90));
        NearPoint((0, -3.4), LayoutGeometry.Rotate(0, 3.4, 180));
        NearPoint((-3.4, 0), LayoutGeometry.Rotate(0, 3.4, 270));
    }

    [Fact]
    public void Handoff_point_and_facing_follow_the_rotation()
    {
        var lathe = new LayoutModule { Kind = ModuleKind.RoughLathe, Code = "rough-1", X = 10, Z = -5, Rotation = 90 };
        var handoff = LayoutGeometry.HandoffPoint(lathe);
        Assert.NotNull(handoff);
        NearPoint((13.4, -5), handoff.Value);
        NearPoint((1, 0), LayoutGeometry.FacingDirection(lathe));

        var bay = new LayoutModule { Kind = ModuleKind.AgvBayLarge, Code = "agv-01", X = 0, Z = 0, Rotation = 0 };
        var bayHandoff = LayoutGeometry.HandoffPoint(bay);
        Assert.NotNull(bayHandoff);
        NearPoint((0, 0.2), bayHandoff.Value);

        var misc = new LayoutModule { Kind = ModuleKind.MiscStore, Code = "misc-1", X = 0, Z = 0, Rotation = 0 };
        Assert.Null(LayoutGeometry.HandoffPoint(misc));
    }

    [Fact]
    public void Footprint_swaps_width_and_depth_at_quarter_turns()
    {
        var flat = new LayoutModule { Kind = ModuleKind.RoughLathe, Code = "rough-1", X = 0, Z = 0, Rotation = 0 };
        NearRect(new LayoutRect(-3.2, 3.2, -2.4, 2.2), LayoutGeometry.Footprint(flat));

        var turned = new LayoutModule { Kind = ModuleKind.RoughLathe, Code = "rough-1", X = 0, Z = 0, Rotation = 90 };
        // 中心偏移 (0, -0.1) 旋轉 90 度變成 (-0.1, 0)，所以 X 範圍是 -2.4～2.2（偏移跟著模組轉）。
        NearRect(new LayoutRect(-2.4, 2.2, -3.2, 3.2), LayoutGeometry.Footprint(turned));
    }

    [Fact]
    public void Segments_that_share_an_end_do_not_cross_but_a_touching_t_does()
    {
        Assert.False(LayoutGeometry.SegmentsCross((0, 0), (4, 0), (4, 0), (4, 4)));
        Assert.True(LayoutGeometry.SegmentsCross((0, 0), (4, 0), (2, 0), (2, 4)));
        Assert.Equal(1.0, LayoutGeometry.SegmentDistance((0, 0), (4, 0), (0, 1), (4, 1)), 6);
        Assert.Equal(0.0, LayoutGeometry.SegmentDistance((0, 0), (4, 0), (2, -1), (2, 1)), 6);
    }

    [Fact]
    public void Default_layout_has_no_errors_and_no_warnings()
    {
        var result = LayoutValidator.Validate(DefaultLayout.Build());

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Default_layout_road_network_is_the_one_from_step_1b()
    {
        var document = DefaultLayout.Build();

        Assert.Equal(100, document.RoadNodes.Count);
        Assert.Equal(132, document.RoadEdges.Count);

        var nodes = document.RoadNodes.ToDictionary(n => n.Code);
        var lengths = document.RoadEdges
            .Select(e => LayoutGeometry.Distance((nodes[e.From].X, nodes[e.From].Z), (nodes[e.To].X, nodes[e.To].Z)))
            .ToList();
        Assert.Equal(1.6, lengths.Min(), 6);
    }

    [Fact]
    public void Default_layout_stations_sit_on_the_8_metre_grid()
    {
        var document = DefaultLayout.Build();

        Assert.Equal(-12, Module(document, "rough-1").X);
        Assert.Equal(-4, Module(document, "rough-2").X);
        Assert.Equal(4, Module(document, "fine-1").X);
        Assert.Equal(12, Module(document, "fine-2").X);
        Assert.Equal(36, Module(document, "plating-1").X);
        Assert.Equal(-14.4, Module(document, "agv-05").X);
    }

    [Fact]
    public void Display_names_follow_the_station_naming_rule()
    {
        var document = DefaultLayout.Build();

        Assert.Equal("粗加工 2 號機", ModuleNaming.DisplayName(document, Module(document, "rough-2")));
        Assert.Equal("Rough #2", ModuleNaming.EnglishName(document, Module(document, "rough-2")));
        Assert.Equal("淬火", ModuleNaming.DisplayName(document, Module(document, "heat-1")));
        Assert.Equal("Quench", ModuleNaming.EnglishName(document, Module(document, "heat-1")));
        Assert.Equal("大型 AGV agv-04", ModuleNaming.DisplayName(document, Module(document, "agv-04")));
        Assert.Equal("Large AGV agv-04", ModuleNaming.EnglishName(document, Module(document, "agv-04")));
        Assert.Equal("原料倉", ModuleNaming.DisplayName(document, Module(document, "raw-store")));
    }

    [Fact]
    public void Floor_size_must_be_between_40_and_100_by_24_and_60()
    {
        Assert.Equal(LayoutIssueSeverity.Error, Issue(Mutate(d => d.Floor.MaxX = -10), "floor-size").Severity);
        Assert.Equal(LayoutIssueSeverity.Error, Issue(Mutate(d => d.Floor.MaxZ = 0), "floor-size").Severity);
    }

    [Fact]
    public void Too_many_modules_is_an_error()
    {
        var result = Mutate(d =>
        {
            for (var i = 1; i <= 110; i++)
            {
                d.Modules.Add(new LayoutModule { Kind = ModuleKind.MiscStore, Code = "misc-" + (100 + i), X = -30, Z = -13 + i * 0.1, Rotation = 0 });
            }
        });

        Assert.Contains(result.Issues, i => i.Code == "too-many");
    }

    [Fact]
    public void Module_codes_must_be_unique_and_follow_their_kind()
    {
        var duplicate = Mutate(d => Module(d, "agv-02").Code = "agv-01");
        Assert.Contains(duplicate.Issues, i => i.Code == "code-invalid" && i.ModuleCodes.Contains("agv-01"));
        Assert.Equal("code-invalid", Issue(Mutate(d => Module(d, "rough-2").Code = "rough-x"), "code-invalid").Code);
        Assert.Equal("code-invalid", Issue(Mutate(d => d.RoadNodes.Add(new LayoutRoadNode { Code = "-12,-9.4", X = -12, Z = -9.4 })), "code-invalid").Code);
    }

    [Fact]
    public void Rotation_must_be_a_quarter_turn()
    {
        Assert.Equal(LayoutIssueSeverity.Error, Issue(Mutate(d => Module(d, "rough-1").Rotation = 45), "rotation-invalid").Severity);
    }

    [Fact]
    public void Module_footprint_must_stay_inside_the_floor()
    {
        var result = Mutate(d => Module(d, "plating-1").X = 60);
        Assert.Contains(result.Issues, i => i.Code == "module-outside" && i.ModuleCodes.Contains("plating-1"));
    }

    [Fact]
    public void Module_footprints_must_not_overlap_but_touching_is_fine()
    {
        var overlapping = Mutate(d => Module(d, "rough-2").X = -11);
        Assert.Contains(overlapping.Issues, i => i.Code == "module-overlap");

        // 相鄰的車床只差 8 m，占地 6.4 m，中間有 1.6 m 的空隙。
        var standard = Mutate(_ => { });
        Assert.DoesNotContain(standard.Issues, i => i.Code == "module-overlap");
    }

    [Fact]
    public void Facilities_must_be_exactly_one_each()
    {
        Assert.Equal("facility-count", Issue(Mutate(d => d.Modules.Remove(Module(d, "raw-store"))), "facility-count").Code);
        Assert.Equal("facility-count", Issue(Mutate(d => d.Modules.Add(new LayoutModule { Kind = ModuleKind.ToolStore, Code = "tool-store", X = 30, Z = 0, Rotation = 0 })), "facility-count").Code);
    }

    [Fact]
    public void Every_process_needs_at_least_one_machine()
    {
        var result = Mutate(d => d.Modules.Remove(Module(d, "heat-1")));

        var issue = Issue(result, "step-missing");
        Assert.Equal(LayoutIssueSeverity.Error, issue.Severity);
        Assert.Contains("淬火", issue.Message);
    }

    [Fact]
    public void A_large_and_a_small_vehicle_are_required()
    {
        Assert.Equal("vehicle-missing", Issue(Mutate(d => d.Modules.Remove(Module(d, "agv-03"))), "vehicle-missing").Code);
        Assert.Equal("vehicle-missing", Issue(Mutate(d => d.Modules.RemoveAll(m => m.Kind == ModuleKind.AgvBayLarge)), "vehicle-missing").Code);
    }

    [Fact]
    public void Edges_must_point_at_existing_distinct_nodes()
    {
        Assert.Equal("edge-invalid", Issue(Mutate(d => AddEdge(d, "-12,-7", "nowhere")), "edge-invalid").Code);
        Assert.Equal("edge-invalid", Issue(Mutate(d => AddEdge(d, "-12,-7", "-12,-7")), "edge-invalid").Code);
        Assert.Equal("edge-invalid", Issue(Mutate(d => AddEdge(d, "-12,-9.4", "-12,-7")), "edge-invalid").Code);
    }

    [Fact]
    public void Edges_must_be_at_least_1_6_metres_long()
    {
        var result = Mutate(d =>
        {
            AddNode(d, "x1", 0, -20);
            AddNode(d, "x2", 0, -18.6);
            AddEdge(d, "x1", "x2");
        });

        Assert.Equal("edge-short", Issue(result, "edge-short").Code);
    }

    [Fact]
    public void Edges_must_not_cross_each_other()
    {
        var result = Mutate(d =>
        {
            AddNode(d, "p1", 0, -20);
            AddNode(d, "p2", 4, -20);
            AddNode(d, "p3", 2, -22);
            AddNode(d, "p4", 2, -18);
            AddEdge(d, "p1", "p2");
            AddEdge(d, "p3", "p4");
        });

        Assert.Equal("edge-crossing", Issue(result, "edge-crossing").Code);
    }

    [Fact]
    public void Edges_without_a_shared_node_must_keep_1_6_metres_apart()
    {
        var result = Mutate(d =>
        {
            AddNode(d, "q1", 0, -20);
            AddNode(d, "q2", 4, -20);
            AddNode(d, "q3", 0, -21);
            AddNode(d, "q4", 4, -21);
            AddEdge(d, "q1", "q2");
            AddEdge(d, "q3", "q4");
        });

        Assert.Equal("edge-clearance", Issue(result, "edge-clearance").Code);
    }

    [Fact]
    public void Edges_must_not_run_through_a_module_footprint()
    {
        var result = Mutate(d =>
        {
            AddNode(d, "t1", -16, -12.8);
            AddNode(d, "t2", -8, -12.8);
            AddEdge(d, "t1", "t2");
        });

        Assert.Contains(result.Issues, i => i.Code == "edge-through-module" && i.ModuleCodes.Contains("rough-1"));
    }

    [Fact]
    public void A_station_needs_a_road_node_with_an_edge_on_its_handoff()
    {
        var missing = Mutate(d => d.RoadNodes.Remove(d.RoadNodes.Single(n => n.Code == "-12,-9.4")));
        Assert.Contains(missing.Issues, i => i.Code == "handoff-missing" && i.ModuleCodes.Contains("rough-1"));

        var noEdge = Mutate(d => d.RoadEdges.RemoveAll(e => e.From == "-12,-9.4" || e.To == "-12,-9.4"));
        Assert.Contains(noEdge.Issues, i => i.Code == "handoff-missing" && i.ModuleCodes.Contains("rough-1"));
    }

    [Fact]
    public void Every_handoff_must_be_reachable_from_the_raw_material_store()
    {
        var result = Mutate(d => d.RoadEdges.RemoveAll(e => e.From == "0,-3.2" || e.To == "0,-3.2"));

        var issue = Issue(result, "unreachable");
        Assert.Contains("刀具庫", issue.Message);
    }

    [Fact]
    public void Unreachable_issue_marks_the_cut_off_handoff_node_and_edges()
    {
        // 刀具庫的交接點接在內圈 0,-5.2：把內圈車道在這個節點兩側的路段剪掉，
        // 剩下「交接點—車道節點」這條支線自成一塊，連不到原料倉。
        var result = Mutate(d => d.RoadEdges.RemoveAll(e =>
            (e.From == "0,-5.2" || e.To == "0,-5.2") && e.From != "0,-3.2" && e.To != "0,-3.2"));

        var issue = Issue(result, "unreachable");
        Assert.Equal(LayoutIssueSeverity.Error, issue.Severity);
        Assert.Contains("0,-3.2", issue.NodeCodes);
        Assert.Contains(issue.Edges, e => (e.From == "0,-3.2" && e.To == "0,-5.2") || (e.From == "0,-5.2" && e.To == "0,-3.2"));
        // 還連得到原料倉的路段不標記。
        Assert.DoesNotContain(issue.Edges, e => e.From == "-12,-9.4" || e.To == "-12,-9.4");
    }

    [Fact]
    public void The_default_layout_has_no_issues_at_all()
    {
        Assert.Empty(LayoutValidator.Validate(DefaultLayout.Build()).Issues);
    }

    [Fact]
    public void A_station_handoff_with_two_edges_is_only_a_warning()
    {
        // 原料倉交接點多接一條路段（往西側的新節點）：停在交接點上的車會擋住路過的車，但不影響套用。
        var result = Mutate(d =>
        {
            AddNode(d, "h1", -18, -9.4);
            AddEdge(d, "-20,-9.4", "h1");
        });

        var warning = Issue(result, "handoff-through");
        Assert.Equal(LayoutIssueSeverity.Warning, warning.Severity);
        Assert.Contains("raw-store", warning.ModuleCodes);
    }

    [Fact]
    public void A_node_without_edges_and_not_a_handoff_is_only_a_warning()
    {
        var result = Mutate(d => AddNode(d, "lonely", 99, 99));

        var warning = Issue(result, "node-isolated");
        Assert.Equal(LayoutIssueSeverity.Warning, warning.Severity);
        Assert.Contains("lonely", warning.NodeCodes);
    }

    [Fact]
    public void Layouts_with_only_warnings_are_still_valid_for_applying()
    {
        var result = Mutate(d => AddNode(d, "lonely", 99, 99));

        Assert.True(result.IsValid);
        Assert.Contains(result.Issues, i => i.Severity == LayoutIssueSeverity.Warning);
    }

    // 文件來自 JSON，可以把任何欄位弄成 null、把種類弄成任意整數。每一種都要回報 document-invalid，而且驗證器不能丟例外。
    // 同時加上一個會觸發其他規則的錯誤（廠房太小），確認結構有錯時其他檢查不會跑。
    private static readonly (string Name, Action<FactoryLayoutDocument> Break)[] StructuralBreaks =
    {
        ("floor is null", d => d.Floor = null!),
        ("modules is null", d => d.Modules = null!),
        ("roadNodes is null", d => d.RoadNodes = null!),
        ("roadEdges is null", d => d.RoadEdges = null!),
        ("everything is null", d => { d.Floor = null!; d.Modules = null!; d.RoadNodes = null!; d.RoadEdges = null!; }),
        ("a module is null", d => d.Modules[3] = null!),
        ("a module code is null", d => Module(d, "rough-1").Code = null!),
        ("a module code is blank", d => Module(d, "rough-1").Code = "  "),
        ("a module kind is not defined (99)", d => Module(d, "rough-1").Kind = (ModuleKind)99),
        ("a module kind is negative", d => Module(d, "rough-1").Kind = (ModuleKind)(-1)),
        ("a road node is null", d => d.RoadNodes[5] = null!),
        ("a road node code is null", d => d.RoadNodes[5].Code = null!),
        ("a road edge is null", d => d.RoadEdges[7] = null!),
        ("a road edge has no from", d => d.RoadEdges[7].From = null!),
        ("a road edge has no to", d => d.RoadEdges[7].To = null!),
    };

    [Fact]
    public void A_document_with_missing_parts_is_document_invalid_and_never_throws()
    {
        var empty = LayoutValidator.Validate(null);
        Assert.False(empty.IsValid);
        Assert.Equal(new[] { "document-invalid" }, empty.Issues.Select(i => i.Code));

        foreach (var (name, change) in StructuralBreaks)
        {
            var document = DefaultLayout.Build();
            document.Floor.MaxX = 0;
            change(document);

            var result = LayoutValidator.Validate(document);

            Assert.False(result.IsValid, name);
            Assert.True(result.Issues.Count > 0, name);
            Assert.True(result.Issues.All(i => i.Code == "document-invalid" && i.Severity == LayoutIssueSeverity.Error), name);
        }
    }

    [Fact]
    public void Structural_errors_name_the_item_and_stay_short_for_a_huge_document()
    {
        var one = Mutate(d => Module(d, "rough-1").Kind = (ModuleKind)99);
        var index = DefaultLayout.Build().Modules.FindIndex(m => m.Code == "rough-1") + 1;
        Assert.Contains($"第 {index} 項", Issue(one, "document-invalid").Message);

        var many = Mutate(d => d.Modules.AddRange(Enumerable.Repeat<LayoutModule>(null!, 50)));
        var message = Issue(many, "document-invalid").Message;
        Assert.Contains("共 50 項", message);
        Assert.True(message.Length < 100, message);
    }

    [Fact]
    public void A_blank_edge_end_is_an_ordinary_edge_error_not_a_structural_one()
    {
        // 只有 null 是結構錯誤；空字串指不到任何節點，照舊回報 edge-invalid。
        var result = Mutate(d => d.RoadEdges.Add(new LayoutRoadEdge { From = "", To = "-12,-7" }));

        Assert.DoesNotContain(result.Issues, i => i.Code == "document-invalid");
        Assert.Contains(result.Issues, i => i.Code == "edge-invalid");
    }
}
