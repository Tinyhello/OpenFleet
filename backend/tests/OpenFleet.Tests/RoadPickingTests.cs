using OpenFleet.Contracts;
using OpenFleet.Layout;
using Xunit;

namespace OpenFleet.Tests;

/// <summary>路線編輯的點選、吸附、鎖軸與驗證標記（純計算，不需要資料庫）。</summary>
public class RoadPickingTests
{
    // 一條水平路段 (0,0)–(10,0)。
    private static FactoryLayoutDocument Line()
    {
        var doc = new FactoryLayoutDocument { Floor = new LayoutFloor { MinX = -20, MaxX = 20, MinZ = -20, MaxZ = 20 } };
        doc.RoadNodes.Add(new LayoutRoadNode { Code = "0,0", X = 0, Z = 0 });
        doc.RoadNodes.Add(new LayoutRoadNode { Code = "10,0", X = 10, Z = 0 });
        doc.RoadEdges.Add(new LayoutRoadEdge { From = "0,0", To = "10,0" });
        return doc;
    }

    [Fact]
    public void NearestNode_respects_the_radius_boundary_and_picks_the_closest()
    {
        var doc = Line();
        Assert.Equal("0,0", RoadPicking.NearestNode(doc, 0.5, 0, LayoutRules.PickNodeRadius));
        Assert.Null(RoadPicking.NearestNode(doc, 0.51, 0, LayoutRules.PickNodeRadius));
        Assert.Equal("10,0", RoadPicking.NearestNode(doc, 9.7, 0.1, LayoutRules.PickNodeRadius));
        Assert.Null(RoadPicking.NearestNode(doc, 5, 0, LayoutRules.PickNodeRadius));
    }

    [Fact]
    public void NearestEdge_uses_distance_to_the_segment_and_returns_the_document_object()
    {
        var doc = Line();
        Assert.Same(doc.RoadEdges[0], RoadPicking.NearestEdge(doc, 5, 0.4, LayoutRules.PickEdgeRadius));
        Assert.Null(RoadPicking.NearestEdge(doc, 5, 0.41, LayoutRules.PickEdgeRadius));
        // 線段外側：距離算到端點，不是到延長線。
        Assert.Null(RoadPicking.NearestEdge(doc, 10.5, 0, LayoutRules.PickEdgeRadius));
        Assert.Same(doc.RoadEdges[0], RoadPicking.NearestEdge(doc, 10.3, 0, LayoutRules.PickEdgeRadius));
    }

    [Fact]
    public void Snap_prefers_node_then_edge_then_grid()
    {
        var doc = Line();

        var node = RoadPicking.Snap(doc, 0.5, 0.3);
        Assert.Equal(RoadSnapKind.Node, node.Kind);
        Assert.Equal("0,0", node.NodeCode);
        Assert.Equal(0, node.X);
        Assert.Equal(0, node.Z);

        var edge = RoadPicking.Snap(doc, 5.03, 0.3);
        Assert.Equal(RoadSnapKind.Edge, edge.Kind);
        Assert.Same(doc.RoadEdges[0], edge.Edge);
        Assert.Equal(5.03, edge.X);
        Assert.Equal(0, edge.Z);

        var grid = RoadPicking.Snap(doc, 5.03, 0.5);
        Assert.Equal(RoadSnapKind.Grid, grid.Kind);
        Assert.Equal(5, grid.X);
        Assert.Equal(0.6, grid.Z);
        Assert.Null(grid.NodeCode);
        Assert.Null(grid.Edge);
    }

    [Fact]
    public void Snap_radius_boundaries()
    {
        var doc = Line();
        Assert.Equal(RoadSnapKind.Node, RoadPicking.Snap(doc, 0.6, 0).Kind);
        Assert.Equal(RoadSnapKind.Edge, RoadPicking.Snap(doc, 0.61, 0.4).Kind);
        Assert.Equal(RoadSnapKind.Grid, RoadPicking.Snap(doc, 0.61, 0.41).Kind);
    }

    [Fact]
    public void Snap_to_an_edge_projects_and_rounds_to_two_decimals()
    {
        var doc = Line();
        var snap = RoadPicking.Snap(doc, 3.456, 0.2);
        Assert.Equal(RoadSnapKind.Edge, snap.Kind);
        Assert.Equal(3.46, snap.X);
        Assert.Equal(0, snap.Z);

        // 斜的路段：投影點離原點最近的位置。
        var diagonal = new FactoryLayoutDocument();
        diagonal.RoadNodes.Add(new LayoutRoadNode { Code = "0,0", X = 0, Z = 0 });
        diagonal.RoadNodes.Add(new LayoutRoadNode { Code = "10,10", X = 10, Z = 10 });
        diagonal.RoadEdges.Add(new LayoutRoadEdge { From = "0,0", To = "10,10" });
        var onDiagonal = RoadPicking.Snap(diagonal, 5.2, 4.8 + 0.2);
        Assert.Equal(RoadSnapKind.Edge, onDiagonal.Kind);
        Assert.Equal(5.1, onDiagonal.X);
        Assert.Equal(5.1, onDiagonal.Z);
    }

    [Fact]
    public void Snap_on_an_empty_network_uses_the_grid()
    {
        var snap = RoadPicking.Snap(new FactoryLayoutDocument(), -1.09, 2.31);
        Assert.Equal(RoadSnapKind.Grid, snap.Kind);
        Assert.Equal(-1.0, snap.X);
        Assert.Equal(2.4, snap.Z);
    }

    [Fact]
    public void LockAxis_keeps_the_larger_axis()
    {
        Assert.Equal((5.0, 0.0), RoadPicking.LockAxis(0, 0, 5, 2));
        Assert.Equal((0.0, -3.0), RoadPicking.LockAxis(0, 0, 1, -3));
        Assert.Equal((2.0, 1.0), RoadPicking.LockAxis(1, 1, 2, 2)); // 相等時保留水平
        Assert.Equal((-4.0, 7.0), RoadPicking.LockAxis(7, 7, -4, 9));
    }

    [Fact]
    public void SnapLocked_accepts_a_node_that_stays_on_the_locked_line()
    {
        var doc = new FactoryLayoutDocument();
        doc.RoadNodes.Add(new LayoutRoadNode { Code = "5.3,0", X = 5.3, Z = 0 });

        var snap = RoadPicking.SnapLocked(doc, 0, 0, 5, 0.2);

        Assert.Equal(RoadSnapKind.Node, snap.Kind);
        Assert.Equal("5.3,0", snap.NodeCode);
        Assert.Equal(5.3, snap.X);
        Assert.Equal(0, snap.Z);
    }

    [Fact]
    public void SnapLocked_rejects_a_node_off_the_line_and_grid_snaps_the_free_axis()
    {
        var doc = new FactoryLayoutDocument();
        doc.RoadNodes.Add(new LayoutRoadNode { Code = "5,0.5", X = 5, Z = 0.5 });

        var snap = RoadPicking.SnapLocked(doc, 0.13, 0.13, 5.03, 0.3);

        Assert.Equal(RoadSnapKind.Grid, snap.Kind);
        Assert.Equal(5.0, snap.X);
        Assert.Equal(0.13, snap.Z); // 鎖定軸維持起點座標
    }

    [Fact]
    public void SnapLocked_vertical_snaps_to_an_edge_crossing_the_line()
    {
        var doc = new FactoryLayoutDocument();
        doc.RoadNodes.Add(new LayoutRoadNode { Code = "-5,5", X = -5, Z = 5 });
        doc.RoadNodes.Add(new LayoutRoadNode { Code = "5,5", X = 5, Z = 5 });
        doc.RoadEdges.Add(new LayoutRoadEdge { From = "-5,5", To = "5,5" });

        var snap = RoadPicking.SnapLocked(doc, 1, 0, 1.2, 4.8);

        Assert.Equal(RoadSnapKind.Edge, snap.Kind);
        Assert.Equal(1, snap.X);
        Assert.Equal(5, snap.Z);
    }

    [Fact]
    public void Issue_marks_split_by_severity_and_normalise_edge_direction()
    {
        var validation = new LayoutValidationDto
        {
            Issues =
            {
                new LayoutIssueDto
                {
                    Code = "edge-short", Severity = LayoutIssueSeverity.Error,
                    Edges = { new LayoutRoadEdge { From = "b", To = "a" } }, NodeCodes = { "a" },
                },
                new LayoutIssueDto
                {
                    Code = "unreachable", Severity = LayoutIssueSeverity.Error,
                    ModuleCodes = { "rough-1" }, NodeCodes = { "h" },
                },
                new LayoutIssueDto { Code = "node-isolated", Severity = LayoutIssueSeverity.Warning, NodeCodes = { "w" } },
                new LayoutIssueDto
                {
                    Code = "handoff-through", Severity = LayoutIssueSeverity.Warning,
                    ModuleCodes = { "fine-1" }, NodeCodes = { "t" }, Edges = { new LayoutRoadEdge { From = "x", To = "y" } },
                },
            },
        };

        var marks = LayoutIssueMarks.From(validation);

        Assert.Equal(new[] { "a", "h" }, marks.ErrorNodeCodes.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(new[] { "t", "w" }, marks.WarningNodeCodes.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(new[] { "rough-1" }, marks.ErrorModuleCodes);
        Assert.True(marks.HasErrorEdge("a", "b"));
        Assert.True(marks.HasErrorEdge("b", "a"));
        Assert.False(marks.HasErrorEdge("x", "y")); // 警告的路段不畫成錯誤
        Assert.Single(marks.ErrorEdgeKeys);
        Assert.Equal(LayoutIssueMarks.EdgeKey("a", "b"), LayoutIssueMarks.EdgeKey("b", "a"));
    }

    [Fact]
    public void Issue_marks_of_null_or_valid_layout_are_empty()
    {
        var empty = LayoutIssueMarks.From(null);
        Assert.Empty(empty.ErrorNodeCodes);
        Assert.Empty(empty.ErrorEdgeKeys);

        var valid = LayoutIssueMarks.From(LayoutValidator.Validate(DefaultLayout.Build()));
        Assert.Empty(valid.ErrorNodeCodes);
        Assert.Empty(valid.WarningNodeCodes);
        Assert.Empty(valid.ErrorModuleCodes);
        Assert.Empty(valid.ErrorEdgeKeys);
    }
}
