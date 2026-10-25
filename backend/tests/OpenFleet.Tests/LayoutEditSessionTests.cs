using OpenFleet.Contracts;
using OpenFleet.Layout;
using Xunit;

namespace OpenFleet.Tests;

/// <summary>編輯模型（LayoutEditSession）的純計算測試：新增、移動、旋轉、刪除與自動接路、復原、廠房大小、擺放檢查。</summary>
public class LayoutEditSessionTests
{
    private static LayoutEditSession NewSession() => new(DefaultLayout.Build());

    private static string Canonical(LayoutEditSession session) => LayoutDocuments.Canonical(session.Document);

    private static List<string> EdgeKeys(FactoryLayoutDocument doc) =>
        doc.RoadEdges
            .Select(e => string.CompareOrdinal(e.From, e.To) <= 0 ? e.From + "|" + e.To : e.To + "|" + e.From)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<LayoutIssueDto> Errors(LayoutEditSession session) =>
        session.Validate().Issues.Where(i => i.Severity == LayoutIssueSeverity.Error);

    [Fact]
    public void Session_works_on_a_copy_of_the_source()
    {
        var source = DefaultLayout.Build();
        var before = LayoutDocuments.Canonical(source);
        var session = new LayoutEditSession(source);

        session.RotateModule("fine-2");

        Assert.Equal(before, LayoutDocuments.Canonical(source));
        Assert.NotSame(source, session.Document);
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void Document_clone_copies_every_property()
    {
        // 新增欄位時要同步 LayoutDocuments.Clone 與 Canonical；這裡的屬性數量檢查會提醒。
        Assert.Equal(4, typeof(FactoryLayoutDocument).GetProperties().Length);
        Assert.Equal(4, typeof(LayoutFloor).GetProperties().Length);
        Assert.Equal(5, typeof(LayoutModule).GetProperties().Length);
        Assert.Equal(3, typeof(LayoutRoadNode).GetProperties().Length);
        Assert.Equal(2, typeof(LayoutRoadEdge).GetProperties().Length);

        var source = DefaultLayout.Build();
        var copy = LayoutDocuments.Clone(source);
        Assert.Equal(LayoutDocuments.Canonical(source), LayoutDocuments.Canonical(copy));
        Assert.NotSame(source.Modules[0], copy.Modules[0]);
        Assert.NotSame(source.RoadEdges[0], copy.RoadEdges[0]);
    }

    [Fact]
    public void Snap_rounds_to_the_nearest_step()
    {
        Assert.Equal(24, LayoutEditSession.Snap(24.09, 0.2));
        Assert.Equal(24.2, LayoutEditSession.Snap(24.11, 0.2));
        Assert.Equal(-0.2, LayoutEditSession.Snap(-0.19, 0.2));
        Assert.Equal(0, LayoutEditSession.Snap(-0.05, 0.2));
        Assert.Equal(50.4, LayoutEditSession.Snap(50.3, 0.4));
    }

    // ---- 新增 ----

    [Fact]
    public void AddModule_snaps_picks_the_next_code_and_connects_the_road()
    {
        var session = NewSession();

        var module = session.AddModule(ModuleKind.RoughLathe, 24.07, 0.23, 180);

        Assert.NotNull(module);
        Assert.Equal("rough-3", module!.Code);
        Assert.Equal((24.0, 0.2, 180), (module.X, module.Z, module.Rotation));
        Assert.Contains(session.Document.Modules, m => m.Code == "rough-3");
        Assert.Contains("24,-3.2|24,-5.2", EdgeKeys(session.Document));
        Assert.Empty(Errors(session));
        Assert.True(session.CanUndo);
        Assert.Equal(3, session.CountOf(ModuleKind.RoughLathe));
    }

    [Fact]
    public void AddModule_normalizes_rotation_and_rejects_odd_angles()
    {
        var session = NewSession();
        Assert.Equal(270, session.AddModule(ModuleKind.MiscStore, 0, 20, -90)!.Rotation);
        Assert.Null(session.AddModule(ModuleKind.MiscStore, 0, 20, 45));
        Assert.Equal(2, session.CountOf(ModuleKind.MiscStore));
    }

    [Fact]
    public void AddModule_decor_has_no_road()
    {
        var session = NewSession();
        var edges = EdgeKeys(session.Document);
        var module = session.AddModule(ModuleKind.CarrierStorage, 30, 0, 0);
        Assert.Equal("carrier-2", module!.Code);
        Assert.Equal(edges, EdgeKeys(session.Document));
    }

    [Fact]
    public void AddModule_agv_bay_gets_the_next_two_digit_code()
    {
        var session = NewSession();
        var module = session.AddModule(ModuleKind.AgvBaySmall, -17.4, 0.2, 0);
        Assert.Equal("agv-06", module!.Code);
        Assert.Equal(6, session.Document.Modules.Count(m => ModuleCatalog.IsBay(m.Kind)));
    }

    [Fact]
    public void Facilities_cannot_be_added_twice_or_removed()
    {
        var session = NewSession();
        foreach (var kind in new[] { ModuleKind.RawMaterialStore, ModuleKind.FinishedGoodsStore, ModuleKind.ShippingDock, ModuleKind.ToolStore })
        {
            Assert.False(session.CanAdd(kind));
            Assert.Null(session.AddModule(kind, 0, 0, 0));
        }

        Assert.False(session.CanRemove("raw-store"));
        Assert.False(session.RemoveModule("raw-store"));
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
        Assert.True(session.CanRemove("rough-1"));
        Assert.False(session.CanRemove("nope"));
    }

    [Fact]
    public void Facility_can_be_added_again_in_a_layout_without_it()
    {
        var doc = DefaultLayout.Build();
        doc.Modules.RemoveAll(m => m.Code == "tool-store");
        var session = new LayoutEditSession(doc);

        Assert.True(session.CanAdd(ModuleKind.ToolStore));
        Assert.Equal("tool-store", session.AddModule(ModuleKind.ToolStore, 0, 0.2, 180)!.Code);
        Assert.False(session.CanAdd(ModuleKind.ToolStore));
    }

    [Fact]
    public void CanAdd_respects_the_module_and_vehicle_limits()
    {
        var doc = DefaultLayout.Build();
        var session = new LayoutEditSession(doc);
        while (session.CountOf(ModuleKind.AgvBayLarge) + session.CountOf(ModuleKind.AgvBaySmall) < LayoutRules.MaxVehicles)
        {
            Assert.NotNull(session.AddModule(ModuleKind.AgvBayLarge, 40, 10, 0));
        }

        Assert.False(session.CanAdd(ModuleKind.AgvBayLarge));
        Assert.False(session.CanAdd(ModuleKind.AgvBaySmall));
        Assert.Null(session.AddModule(ModuleKind.AgvBaySmall, 40, 10, 0));

        while (session.Document.Modules.Count < LayoutRules.MaxModules)
        {
            Assert.NotNull(session.AddModule(ModuleKind.MiscStore, 40, 10, 0));
        }

        Assert.False(session.CanAdd(ModuleKind.MiscStore));
        Assert.False(session.CanAdd(ModuleKind.RoughLathe));
    }

    // ---- 移動、旋轉、刪除 ----

    [Fact]
    public void MoveModule_snaps_and_reconnects_the_road()
    {
        var session = NewSession();

        Assert.True(session.MoveModule("fine-2", 24.03, 0.19));

        var module = session.Document.Modules.Single(m => m.Code == "fine-2");
        Assert.Equal((24.0, 0.2), (module.X, module.Z));
        Assert.DoesNotContain(session.Document.RoadNodes, n => n.Code == "12,-9.4");
        Assert.Contains("24,3.6|24,5.2", EdgeKeys(session.Document));
        Assert.Empty(Errors(session));
    }

    [Fact]
    public void MoveModule_without_a_change_or_for_an_unknown_module_is_a_no_op()
    {
        var session = NewSession();
        Assert.False(session.MoveModule("fine-2", 12.04, -12.79)); // 對齊後還是原位
        Assert.False(session.MoveModule("nope", 0, 0));
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void MoveModule_away_and_back_restores_the_document()
    {
        var session = NewSession();
        var original = Canonical(session);
        var originalEdges = EdgeKeys(session.Document);

        session.MoveModule("fine-2", 24, 0.2);
        session.MoveModule("fine-2", 12, -12.8);

        Assert.Equal(originalEdges, EdgeKeys(session.Document));
        Assert.Equal(
            original.Split('\n').Where(l => l.StartsWith("M ")).ToList(),
            Canonical(session).Split('\n').Where(l => l.StartsWith("M ")).ToList());
    }

    [Fact]
    public void RotateModule_cycles_by_ninety_degrees_and_reconnects()
    {
        var session = NewSession();
        var originalEdges = EdgeKeys(session.Document);

        Assert.True(session.RotateModule("fine-2"));
        var module = session.Document.Modules.Single(m => m.Code == "fine-2");
        Assert.Equal(90, module.Rotation);
        Assert.DoesNotContain(session.Document.RoadNodes, n => n.Code == "12,-9.4");

        session.RotateModule("fine-2");
        session.RotateModule("fine-2");
        session.RotateModule("fine-2");

        Assert.Equal(0, session.Document.Modules.Single(m => m.Code == "fine-2").Rotation);
        Assert.Equal(originalEdges, EdgeKeys(session.Document));
        Assert.False(session.RotateModule("nope"));
    }

    [Fact]
    public void RemoveModule_removes_the_module_and_its_spur()
    {
        var session = NewSession();

        Assert.True(session.RemoveModule("rough-2"));

        Assert.DoesNotContain(session.Document.Modules, m => m.Code == "rough-2");
        Assert.DoesNotContain(session.Document.RoadNodes, n => n.Code == "-4,-9.4");
        Assert.False(session.RemoveModule("rough-2"));
    }

    // ---- 復原與重做 ----

    [Fact]
    public void Undo_and_redo_walk_through_the_history()
    {
        var session = NewSession();
        var s0 = Canonical(session);
        session.AddModule(ModuleKind.RoughLathe, 24, 0.2, 180);
        var s1 = Canonical(session);
        session.RemoveModule("rough-1");
        var s2 = Canonical(session);

        Assert.True(session.Undo());
        Assert.Equal(s1, Canonical(session));
        Assert.True(session.Undo());
        Assert.Equal(s0, Canonical(session));
        Assert.False(session.Undo());
        Assert.True(session.CanRedo);

        Assert.True(session.Redo());
        Assert.Equal(s1, Canonical(session));
        Assert.True(session.Redo());
        Assert.Equal(s2, Canonical(session));
        Assert.False(session.Redo());
    }

    [Fact]
    public void A_new_edit_clears_the_redo_stack()
    {
        var session = NewSession();
        session.RotateModule("fine-2");
        session.Undo();
        Assert.True(session.CanRedo);

        session.RotateModule("fine-1");

        Assert.False(session.CanRedo);
        Assert.False(session.Redo());
    }

    [Fact]
    public void History_keeps_at_most_fifty_steps()
    {
        var session = NewSession();
        for (var i = 0; i < LayoutEditSession.MaxHistory + 10; i++)
        {
            Assert.True(session.RotateModule("misc-1"));
        }

        var undone = 0;
        while (session.Undo())
        {
            undone++;
        }

        Assert.Equal(LayoutEditSession.MaxHistory, undone);
        // 最舊的 10 步被丟掉：回不到最初的狀態（misc-1 原本朝 180，多旋轉的 10 次 = 900 度，停在 0）。
        Assert.Equal(0, session.Document.Modules.Single(m => m.Code == "misc-1").Rotation);
    }

    [Fact]
    public void Failed_operations_leave_no_history()
    {
        var session = NewSession();
        session.AddModule(ModuleKind.ToolStore, 0, 0, 0);
        session.RemoveModule("raw-store");
        session.MoveModule("nope", 1, 1);
        session.RotateModule("nope");
        session.SetFloorSize(76, 30.4);
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }

    // ---- IsDirty ----

    [Fact]
    public void IsDirty_follows_the_saved_state()
    {
        var session = NewSession();
        Assert.False(session.IsDirty);

        session.RotateModule("fine-2");
        Assert.True(session.IsDirty);

        session.Undo();
        Assert.False(session.IsDirty);

        session.Redo();
        Assert.True(session.IsDirty);

        session.MarkSaved();
        Assert.False(session.IsDirty);

        session.RotateModule("fine-2");
        Assert.True(session.IsDirty);
        session.Undo();
        Assert.False(session.IsDirty);
        session.Undo();
        Assert.True(session.IsDirty);
    }

    // ---- 廠房大小 ----

    [Fact]
    public void SetFloorSize_keeps_the_centre_snaps_and_clamps()
    {
        var session = NewSession(); // 中心 (4, 0)，76 × 30.4

        Assert.True(session.SetFloorSize(80.1, 40));

        var floor = session.Document.Floor;
        Assert.Equal((-36.0, 44.0, -20.0, 20.0), (floor.MinX, floor.MaxX, floor.MinZ, floor.MaxZ));

        Assert.True(session.SetFloorSize(1000, 1));
        floor = session.Document.Floor;
        Assert.Equal(100.0, floor.MaxX - floor.MinX, 6);
        Assert.Equal(24.0, floor.MaxZ - floor.MinZ, 6);
        Assert.Equal(4.0, (floor.MinX + floor.MaxX) / 2, 6);

        Assert.True(session.SetFloorSize(0, 1000));
        floor = session.Document.Floor;
        Assert.Equal(40.0, floor.MaxX - floor.MinX, 6);
        Assert.Equal(60.0, floor.MaxZ - floor.MinZ, 6);
    }

    [Fact]
    public void SetFloorSize_coordinates_are_rounded_to_a_centimetre()
    {
        var session = NewSession();
        session.SetFloorSize(77.2, 31.6);
        var floor = session.Document.Floor;
        Assert.Equal(RoadGraphEditor.Round(floor.MinX), floor.MinX);
        Assert.Equal(RoadGraphEditor.Round(floor.MaxZ), floor.MaxZ);
        Assert.Equal(77.2, floor.MaxX - floor.MinX, 6);
    }

    [Fact]
    public void Consecutive_floor_size_changes_are_one_undo_step()
    {
        var session = NewSession();
        var original = Canonical(session);

        session.SetFloorSize(76.8, 30.4);
        session.SetFloorSize(78, 30.4);
        session.SetFloorSize(80, 32);

        Assert.True(session.Undo());
        Assert.Equal(original, Canonical(session));
        Assert.False(session.CanUndo);

        Assert.True(session.Redo());
        Assert.Equal(80.0, session.Document.Floor.MaxX - session.Document.Floor.MinX, 6);
    }

    [Fact]
    public void Another_edit_ends_the_floor_size_drag()
    {
        var session = NewSession();
        var original = Canonical(session);
        session.SetFloorSize(80, 30.4);
        var afterFirst = Canonical(session);
        session.RotateModule("fine-2");
        session.SetFloorSize(84, 30.4);

        session.Undo(); // 第二段拖曳
        Assert.Equal(80.0, session.Document.Floor.MaxX - session.Document.Floor.MinX, 6);
        session.Undo(); // 旋轉
        session.Undo(); // 第一段拖曳
        Assert.Equal(original, Canonical(session));
        Assert.NotEqual(afterFirst, original);
    }

    [Fact]
    public void Undo_ends_the_floor_size_drag()
    {
        var session = NewSession();
        session.RotateModule("fine-2");
        session.SetFloorSize(80, 30.4);
        session.Undo();
        session.SetFloorSize(82, 30.4); // 復原之後的新操作不是併入舊的一步
        session.SetFloorSize(84, 30.4);

        Assert.True(session.Undo());
        Assert.Equal(76.0, session.Document.Floor.MaxX - session.Document.Floor.MinX, 6);
        Assert.Equal(90, session.Document.Modules.Single(m => m.Code == "fine-2").Rotation);
    }

    // ---- 擺放檢查 ----

    [Fact]
    public void CanPlace_requires_a_free_spot_inside_the_floor()
    {
        var session = NewSession();

        Assert.True(session.CanPlace(ModuleKind.RoughLathe, 24, 0.2, 180, null));
        Assert.False(session.CanPlace(ModuleKind.RoughLathe, 12, -12.8, 0, null)); // 壓在 fine-2 上
        Assert.True(session.CanPlace(ModuleKind.FineLathe, 12, -12.8, 0, "fine-2")); // 拖曳 fine-2 本身
        Assert.False(session.CanPlace(ModuleKind.RoughLathe, 100, 0, 0, null)); // 超出廠房
        Assert.False(session.CanPlace(ModuleKind.RoughLathe, 4, -14, 0, "fine-1")); // 下緣超出
        Assert.False(session.CanPlace(ModuleKind.RoughLathe, 24, 0.2, 45, null));
    }

    [Fact]
    public void CanPlace_allows_touching_edges_but_not_overlap()
    {
        var session = NewSession();
        // 旋轉 90 度的車床占地 x 寬 4.6：中心 -6.5 時左緣剛好貼著 rough-1 的右緣（-8.8）。
        Assert.True(session.CanPlace(ModuleKind.RoughLathe, -6.4, -10, 90, "rough-2"));
        Assert.False(session.CanPlace(ModuleKind.RoughLathe, -6.6, -10, 90, "rough-2"));
    }

    [Fact]
    public void Validate_reports_the_errors_of_the_draft()
    {
        var session = NewSession();
        Assert.Empty(Errors(session));

        session.MoveModule("fine-2", 4, -12.8); // 壓在 fine-1 上
        Assert.NotEmpty(Errors(session));

        session.Undo();
        Assert.Empty(Errors(session));
    }

    private static void AssertSameRoads(FactoryLayoutDocument expected, FactoryLayoutDocument actual)
    {
        Assert.Equal(expected.RoadNodes.Select(n => n.Code).OrderBy(c => c, StringComparer.Ordinal),
            actual.RoadNodes.Select(n => n.Code).OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(EdgeKeys(expected), EdgeKeys(actual));
    }

    [Fact]
    public void Moving_fine_2_near_a_lane_node_connects_to_that_node_without_errors()
    {
        // 互動驗收發現：命中點 (15.6, 5.2) 離節點 16,5.2 只有 0.4 m，切段會留下 0.4 m 的短路段。
        var session = NewSession();
        var original = DefaultLayout.Build();

        Assert.True(session.MoveModule("fine-2", 15.6, 0.2));

        Assert.Contains(session.Document.RoadEdges, e => (e.From == "16,5.2" || e.To == "16,5.2") && (e.From.StartsWith("15.6,") || e.To.StartsWith("15.6,")));
        Assert.DoesNotContain(session.Document.RoadNodes, n => n.Code == "15.6,5.2");
        Assert.Empty(Errors(session));

        Assert.True(session.MoveModule("fine-2", 12, -12.8));
        AssertSameRoads(original, session.Document);
    }

    [Fact]
    public void Adding_a_rough_lathe_near_a_lane_node_connects_to_that_node_without_errors()
    {
        var session = NewSession();
        var original = DefaultLayout.Build();

        var lathe = session.AddModule(ModuleKind.RoughLathe, -20.4, 0.0, 180);

        Assert.NotNull(lathe);
        Assert.Contains(session.Document.RoadEdges, e => e.From == "-20,-5.2" || e.To == "-20,-5.2");
        Assert.DoesNotContain(session.Document.RoadNodes, n => n.Code == "-20.4,-5.2");
        Assert.Empty(Errors(session));

        Assert.True(session.RemoveModule(lathe!.Code));
        AssertSameRoads(original, session.Document);
    }
}
