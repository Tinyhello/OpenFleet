using OpenFleet.Contracts;
using OpenFleet.Layout;
using Xunit;

namespace OpenFleet.Tests;

/// <summary>編輯模型的路線操作（畫路、移動節點、刪除節點與路段）：每個成功的操作一步復原，失敗不留紀錄。</summary>
public class RoadEditSessionTests
{
    private static LayoutEditSession NewSession() => new(DefaultLayout.Build());

    private static string Canonical(LayoutEditSession session) => LayoutDocuments.Canonical(session.Document);

    private static bool HasEdge(LayoutEditSession session, string a, string b) => RoadGraphEditor.HasEdge(session.Document, a, b);

    private static bool HasNode(LayoutEditSession session, string code) => RoadGraphEditor.FindNode(session.Document, code) != null;

    // 在預設佈局空曠處畫一條垂直路：(2,0) 到 (2,12)，穿過內圈與外圈車道。
    private static List<string> DrawCrossingRoad(LayoutEditSession session) =>
        session.AddRoad(2, 0, 2, 12) ?? throw new Xunit.Sdk.XunitException("AddRoad returned null");

    [Fact]
    public void AddRoad_crossing_lanes_creates_junctions_and_short_segments_and_undo_redo_restores()
    {
        var session = NewSession();
        var original = Canonical(session);

        var codes = DrawCrossingRoad(session);
        var drawn = Canonical(session);

        Assert.Equal("2,0", codes[0]);
        Assert.Equal("2,12", codes[^1]);
        Assert.Contains("2,5.2", codes);
        Assert.Contains("2,7", codes);
        for (var i = 0; i + 1 < codes.Count; i++)
        {
            Assert.True(HasEdge(session, codes[i], codes[i + 1]));
            var a = RoadGraphEditor.FindNode(session.Document, codes[i])!;
            var b = RoadGraphEditor.FindNode(session.Document, codes[i + 1])!;
            Assert.True(LayoutGeometry.Distance((a.X, a.Z), (b.X, b.Z)) <= LayoutRules.MaxSegmentLength + 1e-6);
        }

        Assert.True(session.CanUndo);
        Assert.True(session.Undo());
        Assert.Equal(original, Canonical(session));
        Assert.True(session.Redo());
        Assert.Equal(drawn, Canonical(session));
        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void AddRoad_that_changes_nothing_returns_null_and_keeps_history_empty()
    {
        var session = NewSession();

        // 起點終點落在同一個既有節點（0.3 m 內合併）。
        Assert.Null(session.AddRoad(-12, -7, -12.1, -7.1));
        // 整條路都已存在。
        Assert.Null(session.AddRoad(-12, -9.4, -12, -7));

        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void MoveNode_moves_and_reattaches_edges_with_one_undo_step()
    {
        var session = NewSession();
        DrawCrossingRoad(session);
        var before = Canonical(session);

        var code = session.MoveNode("2,2.6", 3, 2.6);

        Assert.Equal("3,2.6", code);
        Assert.False(HasNode(session, "2,2.6"));
        Assert.True(HasEdge(session, "2,0", "3,2.6"));
        Assert.True(HasEdge(session, "3,2.6", "2,5.2"));

        Assert.True(session.Undo());
        Assert.Equal(before, Canonical(session));
        Assert.True(session.Redo());
        Assert.True(HasNode(session, "3,2.6"));
    }

    [Fact]
    public void MoveNode_within_merge_distance_merges_without_self_loops_or_duplicates()
    {
        var session = NewSession();
        DrawCrossingRoad(session);
        var before = Canonical(session);

        var code = session.MoveNode("2,2.6", 2, 0.2);

        Assert.Equal("2,0", code);
        Assert.False(HasNode(session, "2,2.6"));
        Assert.True(HasEdge(session, "2,0", "2,5.2"));
        Assert.DoesNotContain(session.Document.RoadEdges, e => e.From == e.To);
        var keys = session.Document.RoadEdges.Select(e => string.CompareOrdinal(e.From, e.To) <= 0 ? e.From + "|" + e.To : e.To + "|" + e.From).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());

        Assert.True(session.Undo());
        Assert.Equal(before, Canonical(session));
    }

    [Fact]
    public void MoveNode_unknown_or_unchanged_returns_null_without_history()
    {
        var session = NewSession();
        DrawCrossingRoad(session);
        session.Undo();
        session.Redo();
        var undoCount = 0;
        while (session.Undo())
        {
            undoCount++;
        }

        Assert.Equal(1, undoCount);
        session.Redo();

        Assert.Null(session.MoveNode("nope", 1, 1));
        Assert.Null(session.MoveNode("2,2.6", 2, 2.6));
        Assert.True(session.Undo()); // 還是只有畫路那一步
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void Handoff_nodes_cannot_be_moved_or_deleted()
    {
        var session = NewSession();
        var original = Canonical(session);

        Assert.True(session.IsHandoffNode("-12,-9.4"));
        Assert.False(session.IsHandoffNode("-12,-7"));
        Assert.False(session.IsHandoffNode("nope"));

        Assert.Null(session.MoveNode("-12,-9.4", -12, -11));
        Assert.False(session.DeleteNode("-12,-9.4"));

        Assert.Equal(original, Canonical(session));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void DeleteEdge_keeps_handoff_nodes_and_removes_isolated_plain_endpoints()
    {
        var session = NewSession();

        // 交接點的支線：交接點節點與車道節點都保留（車道節點還有別的路段）。
        Assert.True(session.DeleteEdge("-12,-9.4", "-12,-7"));
        Assert.True(HasNode(session, "-12,-9.4"));
        Assert.True(HasNode(session, "-12,-7"));
        Assert.False(HasEdge(session, "-12,-9.4", "-12,-7"));
        Assert.True(session.Undo());
        Assert.True(HasEdge(session, "-12,-9.4", "-12,-7"));

        // 自己畫的路：刪掉末端那段，沒有路段又不是交接點的終點一起消失。
        DrawCrossingRoad(session);
        var end = session.Document.RoadEdges.Single(e => e.From == "2,12" || e.To == "2,12");
        Assert.True(session.DeleteEdge(end.From, end.To));
        Assert.False(HasNode(session, "2,12"));

        Assert.False(session.DeleteEdge("nope", "nada"));
    }

    [Fact]
    public void DeleteNode_removes_its_edges_with_one_undo_step()
    {
        var session = NewSession();
        DrawCrossingRoad(session);
        var before = Canonical(session);

        Assert.True(session.DeleteNode("2,2.6"));

        Assert.False(HasNode(session, "2,2.6"));
        Assert.DoesNotContain(session.Document.RoadEdges, e => e.From == "2,2.6" || e.To == "2,2.6");
        Assert.False(session.DeleteNode("2,2.6"));

        Assert.True(session.Undo());
        Assert.Equal(before, Canonical(session));
    }

    [Fact]
    public void AddRoad_through_the_lanes_near_nodes_makes_no_short_edges_and_undo_restores()
    {
        // 互動驗收的實測：X ≈ −17.19 從下方內圈車道畫到上方外圈車道；使用者決定（2026-10-25）接到最近的節點。
        var session = NewSession();
        var original = Canonical(session);
        var before = session.Validate();
        Assert.Empty(before.Issues);

        var codes = session.AddRoad(-17.19, -5.2, -17.19, 7);

        Assert.NotNull(codes);
        Assert.Equal(new[] { "-16,-5.2", "-16,5.2", "-16,7" }, codes!.Where(c => c is "-16,-5.2" or "-16,5.2" or "-16,7"));
        Assert.Equal("-16,-5.2", codes[0]);
        Assert.Equal("-16,7", codes[^1]);
        foreach (var edge in session.Document.RoadEdges)
        {
            var a = RoadGraphEditor.FindNode(session.Document, edge.From)!;
            var b = RoadGraphEditor.FindNode(session.Document, edge.To)!;
            Assert.True(LayoutGeometry.Distance((a.X, a.Z), (b.X, b.Z)) >= LayoutRules.MinEdgeLength - 1e-6, $"{edge.From}|{edge.To}");
        }

        foreach (var junction in new[] { "-17.19,7", "-17.19,-5.2", "-17.19,5.2" })
        {
            Assert.False(HasNode(session, junction), junction);
        }

        var after = session.Validate();
        Assert.DoesNotContain(after.Issues, i => i.Code == "edge-short");
        Assert.DoesNotContain(after.Issues, i => i.Code == "edge-clearance");
        Assert.True(after.IsValid, string.Join("; ", after.Issues.Select(i => i.Code + " " + i.Message)));

        var drawn = Canonical(session);
        Assert.True(session.Undo());
        Assert.Equal(original, Canonical(session));
        Assert.True(session.Redo());
        Assert.Equal(drawn, Canonical(session));
        Assert.True(session.Undo());
        Assert.Equal(original, Canonical(session));
    }
}
