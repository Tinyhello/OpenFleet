using System.Globalization;
using OpenFleet.Contracts;
using OpenFleet.Layout;
using Xunit;

namespace OpenFleet.Tests;

/// <summary>
/// 路網編輯（RoadGraphEditor）與模組代碼的純計算測試（不需要資料庫）。
/// 小文件用來測單一規則；預設佈局用來測「移動車床再移回來，路網和原本完全相同」。
/// </summary>
public class RoadGraphEditorTests
{
    private static FactoryLayoutDocument Empty() => new() { Floor = new LayoutFloor { MinX = -20, MaxX = 20, MinZ = -20, MaxZ = 20 } };

    private static void Node(FactoryLayoutDocument doc, double x, double z) =>
        doc.RoadNodes.Add(new LayoutRoadNode { Code = RoadGraphEditor.NodeCode(x, z), X = x, Z = z });

    private static void Edge(FactoryLayoutDocument doc, string from, string to) =>
        doc.RoadEdges.Add(new LayoutRoadEdge { From = from, To = to });

    private static void Link(FactoryLayoutDocument doc, (double X, double Z) a, (double X, double Z) b)
    {
        foreach (var p in new[] { a, b })
        {
            if (RoadGraphEditor.FindNode(doc, RoadGraphEditor.NodeCode(p.X, p.Z)) == null)
            {
                Node(doc, p.X, p.Z);
            }
        }

        Edge(doc, RoadGraphEditor.NodeCode(a.X, a.Z), RoadGraphEditor.NodeCode(b.X, b.Z));
    }

    private static LayoutModule Lathe(string code, double x, double z, int rotation, ModuleKind kind = ModuleKind.RoughLathe) =>
        new() { Code = code, Kind = kind, X = x, Z = z, Rotation = rotation };

    private static string Key(LayoutRoadEdge e) => string.CompareOrdinal(e.From, e.To) <= 0 ? e.From + "|" + e.To : e.To + "|" + e.From;

    private static List<string> EdgeKeys(FactoryLayoutDocument doc) => doc.RoadEdges.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static List<string> NodeKeys(FactoryLayoutDocument doc) =>
        doc.RoadNodes.Select(n => $"{n.Code}@{n.X.ToString(CultureInfo.InvariantCulture)},{n.Z.ToString(CultureInfo.InvariantCulture)}").OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static void AssertSameNetwork(FactoryLayoutDocument expected, FactoryLayoutDocument actual)
    {
        Assert.Equal(NodeKeys(expected), NodeKeys(actual));
        Assert.Equal(EdgeKeys(expected), EdgeKeys(actual));
    }

    private static void AssertConsistent(FactoryLayoutDocument doc)
    {
        var codes = doc.RoadNodes.Select(n => n.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(doc.RoadEdges, e =>
        {
            Assert.Contains(e.From, codes);
            Assert.Contains(e.To, codes);
            Assert.NotEqual(e.From, e.To);
        });
        var keys = EdgeKeys(doc);
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    // ---- NodeCode ----

    [Theory]
    [InlineData(1.0, 2.0, "1,2")]
    [InlineData(-0.0, 5.0, "0,5")]
    [InlineData(-0.001, 0.004, "0,0")]
    [InlineData(1.234, -5.678, "1.23,-5.68")]
    [InlineData(-28.2, 5.2, "-28.2,5.2")]
    [InlineData(3.0, -2.5, "3,-2.5")]
    public void NodeCode_has_at_most_two_decimals_and_no_negative_zero(double x, double z, string expected)
    {
        Assert.Equal(expected, RoadGraphEditor.NodeCode(x, z));
    }

    [Fact]
    public void NodeCode_ignores_the_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ","; // 像德文那樣用逗號當小數點
            CultureInfo.CurrentCulture = comma;
            Assert.Equal("1.5,-2.25", RoadGraphEditor.NodeCode(1.5, -2.25));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Default_layout_node_codes_are_exactly_NodeCode_of_their_coordinates()
    {
        var doc = DefaultLayout.Build();
        Assert.All(doc.RoadNodes, n => Assert.Equal(RoadGraphEditor.NodeCode(n.X, n.Z), n.Code));
    }

    // ---- EnsureNode / SplitEdge ----

    [Fact]
    public void EnsureNode_reuses_a_node_within_merge_distance()
    {
        var doc = Empty();
        Node(doc, 0, 0);
        Node(doc, 5, 0);

        Assert.Equal("0,0", RoadGraphEditor.EnsureNode(doc, 0.2, 0.2));
        Assert.Equal("0,0", RoadGraphEditor.EnsureNode(doc, 0.3, 0));
        Assert.Equal(2, doc.RoadNodes.Count);
        var far = RoadGraphEditor.EnsureNode(doc, 0.4, 0);
        Assert.Equal("0.4,0", far);
        Assert.Equal(3, doc.RoadNodes.Count);
    }

    [Fact]
    public void EnsureNode_picks_the_nearest_of_several_candidates()
    {
        var doc = Empty();
        Node(doc, 0, 0);
        Node(doc, 0.5, 0);
        Assert.Equal("0.5,0", RoadGraphEditor.EnsureNode(doc, 0.4, 0));
    }

    [Fact]
    public void EnsureNode_splits_the_edge_when_the_point_is_on_it()
    {
        var doc = Empty();
        Link(doc, (0, 0), (4, 0));

        var code = RoadGraphEditor.EnsureNode(doc, 2, 0.005);

        Assert.Equal("2,0.01", code);
        Assert.Equal(new[] { "0,0|2,0.01", "2,0.01|4,0" }, EdgeKeys(doc));
        AssertConsistent(doc);
    }

    [Fact]
    public void EnsureNode_adds_a_new_node_when_nothing_is_near()
    {
        var doc = Empty();
        Link(doc, (0, 0), (4, 0));

        var code = RoadGraphEditor.EnsureNode(doc, 2, 0.5);

        Assert.Equal("2,0.5", code);
        Assert.Equal(3, doc.RoadNodes.Count);
        Assert.Single(doc.RoadEdges);
    }

    [Fact]
    public void EnsureNode_rounds_coordinates_to_a_centimetre()
    {
        var doc = Empty();
        var code = RoadGraphEditor.EnsureNode(doc, 1.2345, -0.0004);
        Assert.Equal("1.23,0", code);
        var node = doc.RoadNodes.Single();
        Assert.Equal(1.23, node.X);
        Assert.Equal(0, node.Z);
    }

    [Fact]
    public void SplitEdge_replaces_the_edge_with_two_through_the_new_node()
    {
        var doc = Empty();
        Link(doc, (0, 0), (4, 0));

        var code = RoadGraphEditor.SplitEdge(doc, "4,0", "0,0", 2, 0);

        Assert.Equal("2,0", code);
        Assert.Equal(new[] { "0,0|2,0", "2,0|4,0" }, EdgeKeys(doc));
        Assert.Equal(3, doc.RoadNodes.Count);
    }

    [Fact]
    public void SplitEdge_returns_null_when_the_edge_does_not_exist()
    {
        var doc = Empty();
        Link(doc, (0, 0), (4, 0));
        Assert.Null(RoadGraphEditor.SplitEdge(doc, "0,0", "9,9", 2, 0));
        Assert.Single(doc.RoadEdges);
    }

    // ---- AddRoad ----

    [Fact]
    public void AddRoad_splits_a_long_road_into_equal_segments_of_at_most_four_metres()
    {
        var doc = Empty();

        var codes = RoadGraphEditor.AddRoad(doc, (0, 0), (10, 0));

        Assert.Equal(new[] { "0,0", "3.33,0", "6.67,0", "10,0" }, codes);
        Assert.Equal(3, doc.RoadEdges.Count);
        Assert.All(doc.RoadEdges, e =>
        {
            var a = RoadGraphEditor.FindNode(doc, e.From)!;
            var b = RoadGraphEditor.FindNode(doc, e.To)!;
            Assert.True(LayoutGeometry.Distance((a.X, a.Z), (b.X, b.Z)) <= LayoutRules.MaxSegmentLength);
        });
        AssertConsistent(doc);
    }

    [Fact]
    public void AddRoad_keeps_a_short_road_as_one_segment()
    {
        var doc = Empty();
        var codes = RoadGraphEditor.AddRoad(doc, (0, 0), (4, 0));
        Assert.Equal(new[] { "0,0", "4,0" }, codes);
        Assert.Single(doc.RoadEdges);
    }

    [Fact]
    public void AddRoad_with_the_same_start_and_end_does_nothing_but_return_the_node()
    {
        var doc = Empty();
        Node(doc, 0, 0);
        var codes = RoadGraphEditor.AddRoad(doc, (0, 0), (0.1, 0.1));
        Assert.Equal(new[] { "0,0" }, codes);
        Assert.Empty(doc.RoadEdges);
        Assert.Single(doc.RoadNodes);
    }

    [Fact]
    public void AddRoad_turns_a_crossing_into_a_junction()
    {
        var doc = Empty();
        Link(doc, (0, -5), (0, 5));

        var codes = RoadGraphEditor.AddRoad(doc, (-3, 0), (3, 0));

        Assert.Equal(new[] { "-3,0", "0,0", "3,0" }, codes);
        Assert.Equal(new[] { "-3,0|0,0", "0,-5|0,0", "0,0|0,5", "0,0|3,0" }, EdgeKeys(doc));
        AssertConsistent(doc);
    }

    [Fact]
    public void AddRoad_ending_on_an_existing_edge_makes_a_T_junction()
    {
        var doc = Empty();
        Link(doc, (0, -5), (0, 5));

        var codes = RoadGraphEditor.AddRoad(doc, (-3, 0), (0, 0));

        Assert.Equal(new[] { "-3,0", "0,0" }, codes);
        Assert.Equal(new[] { "-3,0|0,0", "0,-5|0,0", "0,0|0,5" }, EdgeKeys(doc));
    }

    [Fact]
    public void AddRoad_reuses_existing_nodes_and_edges()
    {
        var doc = Empty();
        Link(doc, (0, 0), (4, 0));

        var codes = RoadGraphEditor.AddRoad(doc, (0, 0), (8, 0));

        Assert.Equal(new[] { "0,0", "4,0", "8,0" }, codes);
        Assert.Equal(new[] { "0,0|4,0", "4,0|8,0" }, EdgeKeys(doc));
        Assert.Equal(3, doc.RoadNodes.Count);
    }

    [Fact]
    public void AddRoad_includes_an_existing_node_that_lies_on_the_new_road()
    {
        var doc = Empty();
        Link(doc, (2, 0), (2, 3));

        var codes = RoadGraphEditor.AddRoad(doc, (0, 0), (4, 0));

        Assert.Equal(new[] { "0,0", "2,0", "4,0" }, codes);
        Assert.Contains("0,0|2,0", EdgeKeys(doc));
        Assert.Contains("2,0|4,0", EdgeKeys(doc));
        AssertConsistent(doc);
    }

    [Fact]
    public void AddRoad_merges_endpoints_within_merge_distance()
    {
        var doc = Empty();
        Link(doc, (0, 0), (0, 4));

        var codes = RoadGraphEditor.AddRoad(doc, (0.2, 0.1), (3, 0));

        Assert.Equal("0,0", codes[0]);
        Assert.DoesNotContain(doc.RoadNodes, n => n.Code == "0.2,0.1");
        Assert.Contains("0,0|3,0", EdgeKeys(doc));
    }

    // ---- MoveNode ----

    [Fact]
    public void MoveNode_renames_the_node_and_its_edges()
    {
        var doc = Empty();
        Link(doc, (0, 0), (4, 0));
        Link(doc, (4, 0), (4, 4));

        var code = RoadGraphEditor.MoveNode(doc, "4,0", 5, 1);

        Assert.Equal("5,1", code);
        Assert.Equal(new[] { "0,0|5,1", "4,4|5,1" }, EdgeKeys(doc));
        var node = RoadGraphEditor.FindNode(doc, "5,1")!;
        Assert.Equal((5.0, 1.0), (node.X, node.Z));
        Assert.Null(RoadGraphEditor.FindNode(doc, "4,0"));
        AssertConsistent(doc);
    }

    [Fact]
    public void MoveNode_merges_into_a_node_within_merge_distance()
    {
        var doc = Empty();
        Link(doc, (0, 0), (4, 0));
        Link(doc, (4, 0), (8, 0));
        Link(doc, (0, 0), (8, 0)); // 讓合併後出現重複與自連的路段

        var code = RoadGraphEditor.MoveNode(doc, "4,0", 8.1, 0.1);

        Assert.Equal("8,0", code);
        Assert.Equal(new[] { "0,0|8,0" }, EdgeKeys(doc));
        Assert.DoesNotContain(doc.RoadNodes, n => n.Code == "4,0");
        AssertConsistent(doc);
    }

    [Fact]
    public void MoveNode_onto_a_neighbour_drops_the_self_loop()
    {
        var doc = Empty();
        Link(doc, (0, 0), (4, 0));
        var code = RoadGraphEditor.MoveNode(doc, "4,0", 0.2, 0);
        Assert.Equal("0,0", code);
        Assert.Empty(doc.RoadEdges);
        Assert.Single(doc.RoadNodes);
    }

    [Fact]
    public void MoveNode_returns_null_for_an_unknown_node()
    {
        var doc = Empty();
        Assert.Null(RoadGraphEditor.MoveNode(doc, "1,1", 2, 2));
    }

    // ---- DeleteNode / DeleteEdge ----

    [Fact]
    public void DeleteNode_removes_the_node_and_its_edges_only()
    {
        var doc = Empty();
        Link(doc, (0, 0), (4, 0));
        Link(doc, (4, 0), (8, 0));

        Assert.True(RoadGraphEditor.DeleteNode(doc, "4,0"));

        Assert.Empty(doc.RoadEdges);
        Assert.Equal(new[] { "0,0", "8,0" }, doc.RoadNodes.Select(n => n.Code).OrderBy(c => c, StringComparer.Ordinal));
        Assert.False(RoadGraphEditor.DeleteNode(doc, "4,0"));
    }

    [Fact]
    public void DeleteEdge_removes_ends_that_are_left_without_edges()
    {
        var doc = Empty();
        Link(doc, (0, 0), (4, 0));
        Link(doc, (4, 0), (8, 0));

        Assert.True(RoadGraphEditor.DeleteEdge(doc, "8,0", "4,0"));

        Assert.Equal(new[] { "0,0|4,0" }, EdgeKeys(doc));
        Assert.DoesNotContain(doc.RoadNodes, n => n.Code == "8,0");
        Assert.Contains(doc.RoadNodes, n => n.Code == "4,0");
        Assert.False(RoadGraphEditor.DeleteEdge(doc, "8,0", "4,0"));
    }

    [Fact]
    public void DeleteEdge_keeps_a_module_handoff_node()
    {
        var doc = Empty();
        doc.Modules.Add(Lathe("rough-1", 0, 0, 0)); // 交接點 (0, 3.4)
        Link(doc, (0, 3.4), (0, 7));

        Assert.True(RoadGraphEditor.DeleteEdge(doc, "0,3.4", "0,7"));

        Assert.Contains(doc.RoadNodes, n => n.Code == "0,3.4");
        Assert.DoesNotContain(doc.RoadNodes, n => n.Code == "0,7");
    }

    // ---- ConnectModule ----

    [Fact]
    public void ConnectModule_adds_a_single_spur_to_the_first_edge_hit()
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0, 0, 0);
        doc.Modules.Add(module);
        Link(doc, (-4, 7), (4, 7));
        Link(doc, (-4, 10), (4, 10)); // 更遠的一條不會被接

        Assert.True(RoadGraphEditor.ConnectModule(doc, module));

        Assert.Equal(new[] { "-4,10|4,10", "-4,7|0,7", "0,3.4|0,7", "0,7|4,7" }, EdgeKeys(doc));
        AssertConsistent(doc);
    }

    [Fact]
    public void ConnectModule_does_not_subdivide_a_long_spur()
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0, 0, 0);
        doc.Modules.Add(module);
        Link(doc, (-4, 11.4), (4, 11.4)); // 剛好 8 m

        Assert.True(RoadGraphEditor.ConnectModule(doc, module));

        Assert.Contains("0,11.4|0,3.4", EdgeKeys(doc));
        Assert.Equal(4, doc.RoadNodes.Count);
    }

    [Fact]
    public void ConnectModule_reuses_a_node_at_the_hit_point()
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0, 0, 0);
        doc.Modules.Add(module);
        Link(doc, (-4, 7), (0, 7));
        Link(doc, (0, 7), (4, 7));

        RoadGraphEditor.ConnectModule(doc, module);

        Assert.Equal(4, doc.RoadNodes.Count);
        Assert.Contains("0,3.4|0,7", EdgeKeys(doc));
    }

    [Fact]
    public void ConnectModule_splits_and_goes_straight_when_the_hit_is_far_from_both_ends()
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0, 0, 0);
        doc.Modules.Add(module);
        Link(doc, (-2, 7), (2, 7)); // 命中點離兩端各 2 m（>= 1.6）

        Assert.True(RoadGraphEditor.ConnectModule(doc, module));

        Assert.Equal(new[] { "-2,7|0,7", "0,3.4|0,7", "0,7|2,7" }, EdgeKeys(doc));
        AssertConsistent(doc);
    }

    [Fact]
    public void ConnectModule_connects_to_the_nearer_endpoint_when_the_hit_is_too_close_to_it()
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0.4, 0, 0); // 交接點 (0.4, 3.4)，命中 (0.4, 7)，離 0,7 只有 0.4 m
        doc.Modules.Add(module);
        Link(doc, (-4, 7), (0, 7));
        Link(doc, (0, 7), (4, 7));

        Assert.True(RoadGraphEditor.ConnectModule(doc, module));

        Assert.Equal(4, doc.RoadNodes.Count);
        Assert.Contains("0,7|0.4,3.4", EdgeKeys(doc));
        Assert.Equal(3, RoadGraphEditor.EdgeCount(doc, "0,7"));
    }

    [Fact]
    public void ConnectModule_uses_the_nearer_endpoint_of_an_edge_shorter_than_twice_the_minimum()
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0, 0, 0);
        doc.Modules.Add(module);
        Link(doc, (-1, 7), (1.5, 7)); // 2.5 m：命中點離兩端 1 m、1.5 m，都 < 1.6，取較近的 -1,7

        Assert.True(RoadGraphEditor.ConnectModule(doc, module));

        Assert.Equal(3, doc.RoadNodes.Count);
        Assert.Contains("-1,7|0,3.4", EdgeKeys(doc));
    }

    [Fact]
    public void ConnectModule_does_nothing_when_no_road_is_within_eight_metres()
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0, 0, 0);
        doc.Modules.Add(module);
        Link(doc, (-4, 12), (4, 12)); // 8.6 m

        Assert.False(RoadGraphEditor.ConnectModule(doc, module));

        Assert.Single(doc.RoadEdges);
        Assert.Equal(0, RoadGraphEditor.EdgeCount(doc, "0,3.4"));
    }

    [Fact]
    public void ConnectModule_looks_only_in_the_facing_direction()
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0, 0, 180); // 朝 -Z，交接點 (0, -3.4)
        doc.Modules.Add(module);
        Link(doc, (-4, 7), (4, 7)); // 在背後
        Assert.False(RoadGraphEditor.ConnectModule(doc, module));

        Link(doc, (-4, -6), (4, -6));
        Assert.True(RoadGraphEditor.ConnectModule(doc, module));
        Assert.Contains("0,-3.4|0,-6", EdgeKeys(doc));
    }

    [Fact]
    public void ConnectModule_does_nothing_when_already_connected_or_without_handoff()
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0, 0, 0);
        doc.Modules.Add(module);
        Link(doc, (0, 3.4), (0, 7));
        Link(doc, (-4, 7), (0, 7));
        var before = EdgeKeys(doc);
        Assert.False(RoadGraphEditor.ConnectModule(doc, module));
        Assert.Equal(before, EdgeKeys(doc));

        var decor = Lathe("misc-1", 10, 10, 0, ModuleKind.MiscStore);
        Assert.False(RoadGraphEditor.ConnectModule(doc, decor));
        Assert.Equal(before, EdgeKeys(doc));
    }

    // ---- DisconnectModule / RemoveModule ----

    private static (FactoryLayoutDocument Doc, LayoutModule Module) ConnectedLathe(double leftX, double rightZ, double rightX = 2)
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0, 0, 0);
        doc.Modules.Add(module);
        Link(doc, (leftX, 7), (0, 7));
        Link(doc, (0, 7), (rightX, rightZ));
        Link(doc, (0, 3.4), (0, 7));
        return (doc, module);
    }

    [Fact]
    public void DisconnectModule_merges_the_junction_back_into_one_edge()
    {
        var (doc, module) = ConnectedLathe(-2, 7);

        RoadGraphEditor.DisconnectModule(doc, module);

        Assert.Equal(new[] { "-2,7|2,7" }, EdgeKeys(doc));
        Assert.Equal(new[] { "-2,7", "2,7" }, doc.RoadNodes.Select(n => n.Code).OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void DisconnectModule_keeps_a_junction_that_is_not_in_a_straight_line()
    {
        var (doc, module) = ConnectedLathe(-2, 8.5);

        RoadGraphEditor.DisconnectModule(doc, module);

        Assert.Equal(new[] { "-2,7|0,7", "0,7|2,8.5" }, EdgeKeys(doc));
    }

    [Fact]
    public void DisconnectModule_keeps_a_junction_when_the_merged_edge_would_be_too_long()
    {
        var (doc, module) = ConnectedLathe(-3, 7, 3); // 合起來 6 m

        RoadGraphEditor.DisconnectModule(doc, module);

        Assert.Equal(2, doc.RoadEdges.Count);
        Assert.Contains(doc.RoadNodes, n => n.Code == "0,7");
    }

    [Fact]
    public void DisconnectModule_keeps_a_junction_that_is_another_modules_handoff()
    {
        var (doc, module) = ConnectedLathe(-2, 7);
        doc.Modules.Add(Lathe("rough-2", 0, 10.4, 180)); // 交接點 (0, 7)

        RoadGraphEditor.DisconnectModule(doc, module);

        Assert.Equal(new[] { "-2,7|0,7", "0,7|2,7" }, EdgeKeys(doc));
    }

    [Fact]
    public void DisconnectModule_removes_nodes_left_without_edges()
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0, 0, 0);
        doc.Modules.Add(module);
        Link(doc, (0, 3.4), (0, 7)); // 路口只有這一條支線

        RoadGraphEditor.DisconnectModule(doc, module);

        Assert.Empty(doc.RoadEdges);
        Assert.Empty(doc.RoadNodes);
    }

    [Fact]
    public void DisconnectModule_without_a_handoff_node_changes_nothing()
    {
        var doc = Empty();
        var module = Lathe("rough-1", 0, 0, 0);
        Link(doc, (-2, 7), (2, 7));
        var before = EdgeKeys(doc);
        RoadGraphEditor.DisconnectModule(doc, module);
        Assert.Equal(before, EdgeKeys(doc));
        Assert.Equal(2, doc.RoadNodes.Count);
    }

    [Fact]
    public void RemoveModule_disconnects_and_removes_from_the_list()
    {
        var (doc, module) = ConnectedLathe(-2, 7);

        Assert.True(RoadGraphEditor.RemoveModule(doc, "rough-1"));

        Assert.Empty(doc.Modules);
        Assert.Equal(new[] { "-2,7|2,7" }, EdgeKeys(doc));
        Assert.False(RoadGraphEditor.RemoveModule(doc, "rough-1"));
    }

    // ---- 預設佈局：移動車床再移回來 ----

    private static void MoveAndReconnect(FactoryLayoutDocument doc, string code, double x, double z, int rotation)
    {
        var module = doc.Modules.Single(m => m.Code == code);
        var before = LayoutDocuments.Clone(module);
        module.X = x;
        module.Z = z;
        module.Rotation = rotation;
        RoadGraphEditor.ReconnectModule(doc, before, module);
    }

    [Fact]
    public void Default_layout_moving_a_lathe_away_merges_the_old_junction_and_adds_a_new_spur()
    {
        var original = DefaultLayout.Build();
        var doc = DefaultLayout.Build();

        MoveAndReconnect(doc, "fine-2", 24, 0.2, 180);

        Assert.Null(RoadGraphEditor.FindNode(doc, "12,-9.4")); // 舊交接點
        Assert.NotNull(RoadGraphEditor.FindNode(doc, "12,-7")); // 外圈的路口還在（它有四條路段，不會被拿掉）
        Assert.Equal(original.RoadNodes.Count, doc.RoadNodes.Count); // 少一個舊交接點，多一個新交接點
        Assert.Contains("24,-3.2|24,-5.2", EdgeKeys(doc));
        Assert.DoesNotContain(doc.RoadEdges, e => e.From == "12,-9.4" || e.To == "12,-9.4");
        AssertConsistent(doc);

        var errors = LayoutValidator.Validate(doc).Issues.Where(i => i.Severity == LayoutIssueSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void Default_layout_moving_a_lathe_away_and_back_restores_the_road_network()
    {
        var original = DefaultLayout.Build();
        var doc = DefaultLayout.Build();

        MoveAndReconnect(doc, "fine-2", 24, 0.2, 180);
        MoveAndReconnect(doc, "fine-2", 12, -12.8, 0);

        AssertSameNetwork(original, doc);
        Assert.Equal(
            LayoutDocuments.Canonical(original).Split('\n').Where(l => l.StartsWith("M ")),
            LayoutDocuments.Canonical(doc).Split('\n').Where(l => l.StartsWith("M ")));
    }

    [Theory]
    [InlineData("rough-1")]
    [InlineData("heat-1")]
    [InlineData("qc-1")]
    [InlineData("shipping")]
    [InlineData("tool-store")]
    public void Default_layout_disconnect_then_connect_restores_the_road_network(string code)
    {
        var original = DefaultLayout.Build();
        var doc = DefaultLayout.Build();
        var module = doc.Modules.Single(m => m.Code == code);

        RoadGraphEditor.DisconnectModule(doc, module);
        Assert.NotEqual(NodeKeys(original).Count, NodeKeys(doc).Count);
        Assert.True(RoadGraphEditor.ConnectModule(doc, module));

        AssertSameNetwork(original, doc);
    }

    [Fact]
    public void Default_layout_removing_an_agv_bay_merges_the_adjacent_inner_lane_segments()
    {
        var doc = DefaultLayout.Build();

        Assert.True(RoadGraphEditor.RemoveModule(doc, "agv-05")); // 車位在 (-14.4, 0.2)，內圈上邊的路口是 (-14.4, 5.2)

        Assert.Null(RoadGraphEditor.FindNode(doc, "-14.4,0.2"));
        Assert.Null(RoadGraphEditor.FindNode(doc, "-14.4,5.2")); // 兩側 1.8 m 與 1.6 m、一直線，合起來 3.4 m
        Assert.Contains("-12.6,5.2|-16,5.2", EdgeKeys(doc));
        AssertConsistent(doc);
    }

    // ---- ModuleCodes ----

    [Fact]
    public void ModuleCodes_picks_the_smallest_unused_number()
    {
        var doc = DefaultLayout.Build();
        Assert.Equal("rough-3", ModuleCodes.Next(doc, ModuleKind.RoughLathe));
        Assert.Equal("cutting-2", ModuleCodes.Next(doc, ModuleKind.Cutting));
        Assert.Equal("misc-2", ModuleCodes.Next(doc, ModuleKind.MiscStore));
        Assert.Equal("carrier-2", ModuleCodes.Next(doc, ModuleKind.CarrierStorage));

        doc.Modules.RemoveAll(m => m.Code == "rough-1");
        Assert.Equal("rough-1", ModuleCodes.Next(doc, ModuleKind.RoughLathe));
    }

    [Fact]
    public void ModuleCodes_agv_bays_use_two_digits_and_fill_gaps()
    {
        var doc = DefaultLayout.Build();
        Assert.Equal("agv-06", ModuleCodes.Next(doc, ModuleKind.AgvBayLarge));
        Assert.Equal("agv-06", ModuleCodes.Next(doc, ModuleKind.AgvBaySmall));

        doc.Modules.RemoveAll(m => m.Code == "agv-02");
        Assert.Equal("agv-02", ModuleCodes.Next(doc, ModuleKind.AgvBaySmall));

        var many = Empty();
        for (var i = 1; i <= 99; i++)
        {
            many.Modules.Add(Lathe($"agv-{i:00}", i, 0, 0, ModuleKind.AgvBayLarge));
        }

        var next = ModuleCodes.Next(many, ModuleKind.AgvBayLarge);
        Assert.Equal("agv-100", next);
        Assert.True(ModuleCatalog.IsValidCode(ModuleKind.AgvBayLarge, next!));
    }

    [Fact]
    public void ModuleCodes_facilities_return_the_fixed_code_only_when_absent()
    {
        var doc = DefaultLayout.Build();
        foreach (var kind in new[] { ModuleKind.RawMaterialStore, ModuleKind.FinishedGoodsStore, ModuleKind.ShippingDock, ModuleKind.ToolStore })
        {
            Assert.Null(ModuleCodes.Next(doc, kind));
        }

        doc.Modules.RemoveAll(m => m.Code == "tool-store");
        Assert.Equal("tool-store", ModuleCodes.Next(doc, ModuleKind.ToolStore));
    }

    [Fact]
    public void ModuleCodes_generated_codes_always_validate()
    {
        var doc = DefaultLayout.Build();
        doc.Modules.RemoveAll(m => ModuleCatalog.IsFacility(m.Kind));
        foreach (ModuleKind kind in Enum.GetValues(typeof(ModuleKind)))
        {
            var code = ModuleCodes.Next(doc, kind);
            Assert.NotNull(code);
            Assert.True(ModuleCatalog.IsValidCode(kind, code!), $"{kind}: {code}");
        }
    }
}
