using OpenFleet.Api.Layout;
using OpenFleet.Contracts;
using OpenFleet.Layout;
using Xunit;

namespace OpenFleet.Tests;

/// <summary>
/// 廠房形狀（任意多邊形）的純計算測試：幾何、舊文件的相容、驗證規則與編輯操作。
/// 驗證與編輯的測試都從預設佈局（矩形 -34～42 × -15.2～15.2，大門在西牆中點）改起。
/// </summary>
public class FloorShapeTests
{
    private static List<LayoutPoint> Pts(params (double X, double Z)[] points) =>
        points.Select(p => new LayoutPoint { X = p.X, Z = p.Z }).ToList();

    // L 形：右上角（X 大、Z 大）缺一塊。逆時針。
    private static List<LayoutPoint> LShape() => Pts((0, 0), (20, 0), (20, 10), (10, 10), (10, 20), (0, 20));

    // U 形：上方中間缺一塊。
    private static List<LayoutPoint> UShape() => Pts((0, 0), (30, 0), (30, 20), (20, 20), (20, 8), (10, 8), (10, 20), (0, 20));

    // 有斜牆的五邊形。
    private static List<LayoutPoint> Slanted() => Pts((0, 0), (20, 0), (20, 10), (10, 20), (0, 20));

    private static List<LayoutPoint> Reversed(List<LayoutPoint> points) => Enumerable.Reverse(points).ToList();

    // 預設佈局的矩形寫成明確的 Outline（順序與舊文件的矩形相同）。
    private static FactoryLayoutDocument Explicit(Action<FactoryLayoutDocument>? change = null)
    {
        var document = DefaultLayout.Build();
        document.Floor.Outline = LayoutFloorShape.Outline(document.Floor);
        document.Floor.Gate = LayoutFloorShape.Gate(document.Floor);
        change?.Invoke(document);
        return document;
    }

    private static List<LayoutIssueDto> Issues(FactoryLayoutDocument document, string code) =>
        LayoutValidator.Validate(document).Issues.Where(i => i.Code == code).ToList();

    private static LayoutEditSession NewSession() => new(DefaultLayout.Build());

    private static void Near((double X, double Z) expected, LayoutPoint? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
    }

    private static string Canonical(LayoutEditSession session) => LayoutDocuments.Canonical(session.Document);

    // ------------------------------------------------------------------ 幾何

    [Fact]
    public void Signed_area_is_positive_for_counter_clockwise_and_normalization_reverses_clockwise()
    {
        var rect = Pts((0, 0), (10, 0), (10, 5), (0, 5));
        Assert.Equal(50, FloorGeometry.SignedArea(rect), 9);
        Assert.Equal(-50, FloorGeometry.SignedArea(Reversed(rect)), 9);
        Assert.Equal(50, FloorGeometry.Area(Reversed(rect)), 9);
        Assert.Equal(300, FloorGeometry.Area(LShape()), 9);

        var normalized = FloorGeometry.CounterClockwise(Reversed(rect));
        Assert.True(FloorGeometry.SignedArea(normalized) > 0);
        Assert.Equal(rect.Count, normalized.Count);
        var already = FloorGeometry.CounterClockwise(rect);
        Assert.Equal(rect.Select(p => (p.X, p.Z)), already.Select(p => (p.X, p.Z)));
    }

    [Fact]
    public void Point_in_polygon_counts_the_boundary_and_a_small_tolerance()
    {
        var l = LShape();
        Assert.True(FloorGeometry.PointInPolygon(l, 5, 5));
        Assert.True(FloorGeometry.PointInPolygon(l, 15, 5));
        Assert.True(FloorGeometry.PointInPolygon(l, 5, 15));
        Assert.False(FloorGeometry.PointInPolygon(l, 15, 15)); // 缺角
        Assert.False(FloorGeometry.PointInPolygon(l, -1, 5));
        Assert.False(FloorGeometry.PointInPolygon(l, 25, 5));

        Assert.True(FloorGeometry.PointInPolygon(l, 0, 5));    // 在牆上
        Assert.True(FloorGeometry.PointInPolygon(l, 10, 10));  // 凹角的頂點
        Assert.True(FloorGeometry.PointInPolygon(l, 20, 10));  // 頂點
        Assert.True(FloorGeometry.PointInPolygon(l, 15, 10));  // 缺角的牆上
        Assert.True(FloorGeometry.PointInPolygon(l, 20.009, 5));
        Assert.False(FloorGeometry.PointInPolygon(l, 20.02, 5));
        Assert.False(FloorGeometry.PointInPolygon(l, 15, 10.02));
        Assert.False(FloorGeometry.PointInPolygon(Pts((0, 0), (1, 1)), 0.5, 0.5));
    }

    [Fact]
    public void Point_in_polygon_works_for_clockwise_and_slanted_outlines()
    {
        var slanted = Slanted();
        Assert.True(FloorGeometry.PointInPolygon(slanted, 9, 15));
        Assert.False(FloorGeometry.PointInPolygon(slanted, 18, 18)); // 斜牆外
        Assert.True(FloorGeometry.PointInPolygon(slanted, 15, 15));  // 正好在斜牆上
        Assert.True(FloorGeometry.PointInPolygon(Reversed(slanted), 9, 15));
        Assert.False(FloorGeometry.PointInPolygon(Reversed(slanted), 18, 18));
    }

    [Fact]
    public void Rect_inside_polygon_rejects_notches_and_crossing_walls()
    {
        var l = LShape();
        Assert.True(FloorGeometry.RectInsidePolygon(l, new LayoutRect(2, 8, 2, 8)));
        Assert.True(FloorGeometry.RectInsidePolygon(l, new LayoutRect(11, 19, 1, 9)));
        Assert.True(FloorGeometry.RectInsidePolygon(l, new LayoutRect(1, 9, 11, 19)));
        Assert.True(FloorGeometry.RectInsidePolygon(l, new LayoutRect(0, 20, 0, 10)), "貼著牆不算穿過");
        Assert.True(FloorGeometry.RectInsidePolygon(l, new LayoutRect(0, 10, 0, 20)));

        Assert.False(FloorGeometry.RectInsidePolygon(l, new LayoutRect(12, 18, 12, 18)), "整個在缺角裡");
        Assert.False(FloorGeometry.RectInsidePolygon(l, new LayoutRect(8, 12, 8, 12)), "凹角的頂點落在矩形內部");
        Assert.False(FloorGeometry.RectInsidePolygon(l, new LayoutRect(4, 14, 4, 14)), "四角中有一角在缺角");
        Assert.False(FloorGeometry.RectInsidePolygon(l, new LayoutRect(-1, 5, 2, 8)));
    }

    [Fact]
    public void Rect_inside_polygon_handles_a_wall_crossing_without_a_corner_outside()
    {
        // U 形：矩形四個角都在多邊形內，但中間缺的那塊從矩形中間穿過。
        var u = UShape();
        var rect = new LayoutRect(2, 28, 2, 6);
        Assert.True(FloorGeometry.RectInsidePolygon(u, rect));
        Assert.False(FloorGeometry.RectInsidePolygon(u, new LayoutRect(2, 28, 2, 10)));
        // 四個角都在左右兩臂與底部之內，但缺口的牆穿過矩形。
        Assert.True(FloorGeometry.PointInPolygon(u, 5, 10) && FloorGeometry.PointInPolygon(u, 25, 10));
        Assert.False(FloorGeometry.RectInsidePolygon(u, new LayoutRect(5, 25, 10, 12)));
    }

    [Fact]
    public void Rect_inside_polygon_respects_slanted_walls()
    {
        var slanted = Slanted(); // 斜牆：(20,10)→(10,20)，即 x + z = 30
        Assert.True(FloorGeometry.RectInsidePolygon(slanted, new LayoutRect(1, 14, 1, 15)));
        Assert.False(FloorGeometry.RectInsidePolygon(slanted, new LayoutRect(10, 18, 10, 18)));
        Assert.False(FloorGeometry.RectInsidePolygon(slanted, new LayoutRect(12, 19, 12, 19)));
        Assert.True(FloorGeometry.RectInsidePolygon(slanted, new LayoutRect(5, 15, 5, 15)), "角剛好碰到斜牆");
    }

    [Fact]
    public void Segment_inside_polygon_checks_ends_crossings_and_clearance()
    {
        var l = LShape();
        Assert.True(FloorGeometry.SegmentInsidePolygon(l, (2, 2), (18, 8), 0.8));
        Assert.True(FloorGeometry.SegmentInsidePolygon(l, (2, 2), (2, 18), 0.8));
        Assert.False(FloorGeometry.SegmentInsidePolygon(l, (2, 2), (18, 18), 0.8), "終點在缺角");
        Assert.False(FloorGeometry.SegmentInsidePolygon(l, (18, 5), (5, 18), 0.8), "斜穿過缺角");
        Assert.False(FloorGeometry.SegmentInsidePolygon(l, (5, 5), (25, 5), 0.8), "終點在廠房外");
        Assert.False(FloorGeometry.SegmentInsidePolygon(l, (5, 0.5), (15, 0.5), 0.8), "離牆 0.5 m");
        Assert.True(FloorGeometry.SegmentInsidePolygon(l, (5, 0.8), (15, 0.8), 0.8), "剛好 0.8 m");
        Assert.False(FloorGeometry.SegmentInsidePolygon(l, (5, 9.5), (15, 9.5), 0.8), "貼近缺角的牆");
        Assert.False(FloorGeometry.SegmentInsidePolygon(l, (9.5, 5), (9.5, 15), 0.8), "貼近凹角的頂點");
        Assert.True(FloorGeometry.SegmentInsidePolygon(l, (5, 1), (5, 19), 0.8));
        Assert.True(FloorGeometry.SegmentInsidePolygon(l, (5, 0), (15, 0), 0), "clearance 0 時貼牆可以");
        Assert.False(FloorGeometry.SegmentInsidePolygon(l, (15, 5), (15, 15), 0), "clearance 0 仍不能穿牆");
    }

    [Fact]
    public void Segment_inside_polygon_respects_slanted_walls()
    {
        var slanted = Slanted();
        Assert.True(FloorGeometry.SegmentInsidePolygon(slanted, (2, 2), (12, 12), 0.8));
        Assert.False(FloorGeometry.SegmentInsidePolygon(slanted, (2, 2), (14.8, 14.8), 0.8), "離斜牆 0.28 m");
        Assert.False(FloorGeometry.SegmentInsidePolygon(slanted, (2, 2), (17, 17), 0.8));
    }

    [Fact]
    public void Self_intersection_detects_crossing_touching_and_fold_back()
    {
        Assert.Empty(FloorGeometry.SelfIntersectingWalls(Pts((0, 0), (10, 0), (10, 10), (0, 10))));
        Assert.Empty(FloorGeometry.SelfIntersectingWalls(LShape()));
        Assert.Empty(FloorGeometry.SelfIntersectingWalls(UShape()));
        Assert.Empty(FloorGeometry.SelfIntersectingWalls(Slanted()));
        Assert.True(FloorGeometry.IsSimple(LShape()));

        // 領結：第 0 段與第 2 段交叉。
        var bowTie = Pts((0, 0), (10, 10), (10, 0), (0, 10));
        Assert.Equal(new[] { 0, 2 }, FloorGeometry.SelfIntersectingWalls(bowTie));
        Assert.False(FloorGeometry.IsSimple(bowTie));

        // 不相鄰的牆碰到（頂點落在另一段牆上）。
        var touching = Pts((0, 0), (10, 0), (10, 10), (5, 10), (5, 0), (0, 5));
        Assert.Contains(0, FloorGeometry.SelfIntersectingWalls(touching));

        // 折回：(0,0)→(10,0)→(5,0)→(5,5)：第 0、1 段重疊在同一條線上。
        var foldBack = Pts((0, 0), (10, 0), (5, 0), (5, 5));
        var walls = FloorGeometry.SelfIntersectingWalls(foldBack);
        Assert.Contains(0, walls);
        Assert.Contains(1, walls);

        // 最後一段與第一段折回（收尾處）：最後一段 (5,0)→(0,0) 疊在第一段 (0,0)→(10,0) 上。
        var closingFold = Pts((0, 0), (10, 0), (10, 10), (5, 0));
        Assert.Contains(3, FloorGeometry.SelfIntersectingWalls(closingFold));

        // 共線的頂點（同方向）不算折回。
        Assert.Empty(FloorGeometry.SelfIntersectingWalls(Pts((0, 0), (5, 0), (10, 0), (10, 10), (0, 10))));
    }

    public static IEnumerable<object[]> TriangulationShapes()
    {
        yield return new object[] { "rectangle", Pts((0, 0), (10, 0), (10, 5), (0, 5)) };
        yield return new object[] { "convex hexagon", Pts((2, 0), (8, 0), (10, 5), (8, 10), (2, 10), (0, 5)) };
        yield return new object[] { "L", LShape() };
        yield return new object[] { "U", UShape() };
        yield return new object[] { "slanted", Slanted() };
        yield return new object[] { "comb", Pts((0, 0), (30, 0), (30, 20), (25, 20), (25, 5), (20, 5), (20, 20), (15, 20), (15, 5), (10, 5), (10, 20), (5, 20), (5, 5), (0, 5)) };
        yield return new object[] { "collinear vertices", Pts((0, 0), (5, 0), (10, 0), (10, 10), (0, 10), (0, 5)) };
    }

    [Theory]
    [MemberData(nameof(TriangulationShapes))]
    public void Triangulate_covers_the_polygon_with_counter_clockwise_triangles(string name, List<LayoutPoint> outline)
    {
        foreach (var shape in new[] { outline, Reversed(outline) })
        {
            var triangles = FloorGeometry.Triangulate(shape);
            Assert.True(triangles.Length % 3 == 0, name);

            var total = 0.0;
            for (var i = 0; i < triangles.Length; i += 3)
            {
                var a = shape[triangles[i]];
                var b = shape[triangles[i + 1]];
                var c = shape[triangles[i + 2]];
                var area = ((b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X)) / 2;
                Assert.True(area > 0, $"{name}: triangle {i / 3} is not counter-clockwise");
                total += area;
            }

            Assert.Equal(FloorGeometry.Area(shape), total, 6);
            Assert.All(triangles, index => Assert.InRange(index, 0, shape.Count - 1));
        }
    }

    [Fact]
    public void Triangulate_a_triangle_and_degenerate_input()
    {
        Assert.Equal(new[] { 0, 1, 2 }, FloorGeometry.Triangulate(Pts((0, 0), (4, 0), (0, 3))));
        Assert.Equal(3, FloorGeometry.Triangulate(Pts((0, 0), (4, 0), (0, 3))).Length);
        Assert.Empty(FloorGeometry.Triangulate(Pts((0, 0), (4, 0))));
        // 順時針的三角形：輸出改成逆時針。
        var clockwise = Pts((0, 0), (0, 3), (4, 0));
        var triangles = FloorGeometry.Triangulate(clockwise);
        Assert.Equal(3, triangles.Length);
        var a = clockwise[triangles[0]];
        var b = clockwise[triangles[1]];
        var c = clockwise[triangles[2]];
        Assert.True((b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X) > 0);
    }

    [Fact]
    public void Wall_helpers_report_length_angle_normal_and_nearest_wall()
    {
        var l = LShape();
        Assert.Equal(6, FloorGeometry.WallCount(l));
        Assert.Equal(20, FloorGeometry.WallLength(l, 0), 9);
        Assert.Equal(10, FloorGeometry.WallLength(l, 3), 9);
        Assert.Equal(0, FloorGeometry.WallAngle(l, 0), 9);
        Assert.Equal(90, FloorGeometry.WallAngle(l, 1), 9);
        Assert.Equal(180, FloorGeometry.WallAngle(l, 2), 9);
        Assert.Equal(270, FloorGeometry.WallAngle(l, 5), 9);
        Assert.Equal((10.0, 0.0), FloorGeometry.WallMidpoint(l, 0));

        // 逆時針：第 0 段（下緣）的外側朝 -Z；順時針的 outline 結果相同。
        var normal = FloorGeometry.WallOutwardNormal(l, 0);
        Assert.Equal(0, normal.X, 9);
        Assert.Equal(-1, normal.Z, 9);
        var reversed = Reversed(l); // 反過來之後，原本的第 0 段變成第 4 段
        var flipped = FloorGeometry.WallOutwardNormal(reversed, 4);
        Assert.Equal(0, flipped.X, 9);
        Assert.Equal(-1, flipped.Z, 9);

        var hit = FloorGeometry.NearestWall(l, 7, -3)!;
        Assert.Equal(0, hit.Index);
        Assert.Equal(3, hit.Distance, 9);
        Assert.Equal(7, hit.Along, 9);
        Assert.Equal(0.35, hit.T, 9);
        var clamped = FloorGeometry.ProjectOnWall(l, 0, 30, 0);
        Assert.Equal(1, clamped.T, 9);
        Assert.Equal(10, clamped.Distance, 9);
    }

    // ------------------------------------------------------------------ 舊文件的相容

    [Fact]
    public void Old_rectangle_documents_normalize_to_a_counter_clockwise_outline_and_a_west_gate()
    {
        var floor = new LayoutFloor { MinX = -10, MaxX = 30, MinZ = -5, MaxZ = 15 };
        var outline = LayoutFloorShape.Outline(floor);
        Assert.Equal(
            new[] { (-10.0, -5.0), (30.0, -5.0), (30.0, 15.0), (-10.0, 15.0) },
            outline.Select(p => (p.X, p.Z)).ToArray());
        Assert.True(FloorGeometry.SignedArea(outline) > 0);
        Near((-10, 5), LayoutFloorShape.Gate(floor));
        Assert.Equal(3, LayoutFloorShape.GateWallIndex(floor));
        Assert.Equal(4, LayoutFloorShape.WallCount(floor));
        Assert.Equal(800, LayoutFloorShape.Area(floor), 9);

        floor.Outline = new List<LayoutPoint>();
        Assert.Equal(4, LayoutFloorShape.Outline(floor).Count);

        var bounds = LayoutFloorShape.Bounds(floor);
        Assert.Equal((-10.0, 30.0, -5.0, 15.0), (bounds.MinX, bounds.MaxX, bounds.MinZ, bounds.MaxZ));
    }

    [Fact]
    public void Normalized_shape_is_a_copy()
    {
        var floor = new LayoutFloor { MinX = 0, MaxX = 40, MinZ = 0, MaxZ = 24 };
        LayoutFloorShape.Outline(floor)[0].X = 99;
        LayoutFloorShape.Gate(floor).X = 99;
        Assert.Equal(0, LayoutFloorShape.Outline(floor)[0].X);

        floor.Outline = Pts((0, 0), (40, 0), (40, 24), (0, 24));
        floor.Gate = new LayoutPoint { X = 20, Z = 0 };
        LayoutFloorShape.Outline(floor)[0].X = 99;
        LayoutFloorShape.Gate(floor).X = 99;
        Assert.Equal(0, floor.Outline[0].X);
        Assert.Equal(20, floor.Gate.X);
    }

    [Fact]
    public void Implicit_gate_is_the_westmost_wall_midpoint_and_ties_take_the_smaller_z()
    {
        // 斜牆在西邊：最西的是中點 X 最小的那段。
        var floor = new LayoutFloor { Outline = Pts((0, 10), (10, 0), (40, 0), (40, 30), (0, 30)) };
        // 牆中點 X：(5)、25、40、20、0（第 4 段 (0,30)→(0,10)）。
        Near((0, 20), LayoutFloorShape.Gate(floor));

        // 並列：兩段垂直牆的中點 X 都是 0，取中點 Z 較小的。
        var twoWest = new LayoutFloor { Outline = Pts((0, 0), (20, 0), (20, 40), (0, 40), (0, 30), (10, 30), (10, 10), (0, 10)) };
        // 牆中點 X：10、20、10、0（第 3 段 (0,40)→(0,30)）、5、10、5、0（第 7 段 (0,10)→(0,0)）。
        Near((0, 5), LayoutFloorShape.Gate(twoWest));
    }

    [Fact]
    public void Explicit_gate_wins_over_the_implicit_one()
    {
        var floor = new LayoutFloor { MinX = 0, MaxX = 40, MinZ = 0, MaxZ = 24, Gate = new LayoutPoint { X = 20, Z = 24 } };
        Near((20, 24), LayoutFloorShape.Gate(floor));
        Assert.Equal(2, LayoutFloorShape.GateWallIndex(floor));
    }

    [Fact]
    public void Clone_and_canonical_include_outline_and_gate()
    {
        var source = Explicit();
        var copy = LayoutDocuments.Clone(source);
        Assert.NotSame(source.Floor.Outline![0], copy.Floor.Outline![0]);
        Assert.NotSame(source.Floor.Gate, copy.Floor.Gate);
        Assert.Equal(LayoutDocuments.Canonical(source), LayoutDocuments.Canonical(copy));

        copy.Floor.Outline[1].X += 1;
        Assert.NotEqual(LayoutDocuments.Canonical(source), LayoutDocuments.Canonical(copy));
        copy = LayoutDocuments.Clone(source);
        copy.Floor.Gate!.Z += 1;
        Assert.NotEqual(LayoutDocuments.Canonical(source), LayoutDocuments.Canonical(copy));
        copy = LayoutDocuments.Clone(source);
        copy.Floor.Outline = null;
        Assert.NotEqual(LayoutDocuments.Canonical(source), LayoutDocuments.Canonical(copy));

        var plain = LayoutDocuments.Clone(DefaultLayout.Build());
        Assert.Null(plain.Floor.Outline);
        Assert.Null(plain.Floor.Gate);
    }

    [Fact]
    public void Layout_json_omits_null_shape_fields_so_the_default_layout_hash_is_unchanged()
    {
        var json = LayoutJson.Serialize(DefaultLayout.Build());
        Assert.DoesNotContain("outline", json);
        Assert.DoesNotContain("gate", json);
        Assert.Contains("\"floor\":{\"minX\":-34,\"maxX\":42,\"minZ\":-15.2,\"maxZ\":15.2}", json);

        var explicitDocument = Explicit();
        var roundTrip = LayoutJson.Deserialize(LayoutJson.Serialize(explicitDocument));
        Assert.Equal(LayoutDocuments.Canonical(explicitDocument), LayoutDocuments.Canonical(roundTrip));
        Assert.Contains("outline", LayoutJson.Serialize(explicitDocument));
        Assert.NotEqual(LayoutJson.HashOf(DefaultLayout.Build()), LayoutJson.HashOf(explicitDocument));
    }

    // ------------------------------------------------------------------ 驗證

    [Fact]
    public void Default_layout_stays_a_rectangle_and_has_no_issues_with_or_without_explicit_outline()
    {
        var plain = DefaultLayout.Build();
        Assert.Null(plain.Floor.Outline);
        Assert.Null(plain.Floor.Gate);
        Assert.Empty(LayoutValidator.Validate(plain).Issues);
        Assert.Empty(LayoutValidator.Validate(Explicit()).Issues);
    }

    [Fact]
    public void Floor_invalid_reports_too_few_vertices_duplicates_short_walls_crossings_and_zero_area()
    {
        var tooFew = Explicit(d => d.Floor.Outline = Pts((0, 0), (50, 0)));
        Assert.Single(Issues(tooFew, "floor-invalid"));

        var duplicate = Issues(Explicit(d => d.Floor.Outline!.Insert(1, new LayoutPoint { X = d.Floor.Outline[0].X, Z = d.Floor.Outline[0].Z })), "floor-invalid");
        Assert.Contains(duplicate, i => i.WallIndices.SequenceEqual(new[] { 0 }));

        var shortWall = Issues(Explicit(d => d.Floor.Outline![1].X = d.Floor.Outline[0].X + 1.5), "floor-invalid");
        Assert.Contains(shortWall, i => i.WallIndices.Contains(0));

        var bowTie = Issues(Explicit(d => (d.Floor.Outline![1], d.Floor.Outline[2]) = (d.Floor.Outline[2], d.Floor.Outline[1])), "floor-invalid");
        var crossing = Assert.Single(bowTie);
        Assert.Equal(LayoutIssueSeverity.Error, crossing.Severity);
        Assert.Equal(new[] { 0, 2 }, crossing.WallIndices);

        var foldBack = Issues(Explicit(d => d.Floor.Outline = Pts((-34, -15.2), (42, -15.2), (0, -15.2), (0, 15.2))), "floor-invalid");
        Assert.NotEmpty(foldBack);

        var flat = Issues(Explicit(d => d.Floor.Outline = Pts((-34, 0), (0, 0), (42, 0))), "floor-invalid");
        Assert.NotEmpty(flat);

        var tooMany = Issues(
            Explicit(d => d.Floor.Outline = Enumerable.Range(0, LayoutRules.MaxFloorVertices + 1)
                .Select(i => new LayoutPoint { X = 20 * Math.Cos(i * 2 * Math.PI / 65), Z = 20 * Math.Sin(i * 2 * Math.PI / 65) })
                .ToList()),
            "floor-invalid");
        Assert.Single(tooMany);
    }

    [Fact]
    public void Floor_invalid_passes_for_a_clean_polygon()
    {
        Assert.Empty(Issues(Explicit(), "floor-invalid"));
        Assert.Empty(Issues(Explicit(d => d.Floor.Outline![1].X = d.Floor.Outline[0].X + 2), "floor-invalid"));
    }

    [Fact]
    public void Floor_size_uses_the_bounding_box_of_the_outline()
    {
        // L 形：外框仍是 76 × 30.4，缺一個角不影響大小。
        var notch = Explicit(d =>
        {
            d.Floor.Outline = Pts((-34, -15.2), (42, -15.2), (42, 0), (30, 0), (30, 15.2), (-34, 15.2));
            d.Floor.Gate = new LayoutPoint { X = -34, Z = 0 };
        });
        Assert.Empty(Issues(notch, "floor-size"));

        var small = Explicit(d =>
        {
            d.Floor.Outline = Pts((0, 0), (30, 0), (30, 30), (0, 30));
            d.Floor.Gate = new LayoutPoint { X = 0, Z = 15 };
        });
        Assert.Single(Issues(small, "floor-size"));

        // Min／Max 與 Outline 不一致時，以 Outline 為準。
        var stale = Explicit(d => { d.Floor.MinX = 0; d.Floor.MaxX = 1; });
        Assert.Empty(Issues(stale, "floor-size"));
    }

    [Fact]
    public void Gate_invalid_checks_that_the_gate_is_on_a_wall_that_can_hold_it()
    {
        Assert.Empty(Issues(Explicit(), "gate-invalid"));

        var floating = Issues(Explicit(d => d.Floor.Gate = new LayoutPoint { X = 0, Z = 0 }), "gate-invalid");
        var off = Assert.Single(floating);
        Assert.NotEmpty(off.WallIndices);

        var nearly = Explicit(d => d.Floor.Gate = new LayoutPoint { X = -33.96, Z = 0 });
        Assert.Empty(Issues(nearly, "gate-invalid"));
        var tooFar = Explicit(d => d.Floor.Gate = new LayoutPoint { X = -33.9, Z = 0 });
        Assert.Single(Issues(tooFar, "gate-invalid"));

        // 靠牆端太近：門的一端離牆端點不到 0.5 m。
        var nearEnd = Issues(Explicit(d => d.Floor.Gate = new LayoutPoint { X = -34, Z = 10.5 }), "gate-invalid");
        var closeToEnd = Assert.Single(nearEnd);
        Assert.Equal(new[] { 3 }, closeToEnd.WallIndices);
        Assert.Empty(Issues(Explicit(d => d.Floor.Gate = new LayoutPoint { X = -34, Z = 9.7 }), "gate-invalid"));

        // 那段牆太短（10 m，放不下 11 m）：把一段南牆縮成 10 m 的牆。
        var shortWall = Explicit(d =>
        {
            d.Floor.Outline = Pts((-34, -15.2), (-24, -15.2), (-24, -5), (42, -5), (42, 15.2), (-34, 15.2));
            d.Floor.Gate = new LayoutPoint { X = -29, Z = -15.2 };
        });
        var tooShort = Assert.Single(Issues(shortWall, "gate-invalid"));
        Assert.Equal(new[] { 0 }, tooShort.WallIndices);

        // 斜牆上的大門。
        var slanted = Explicit(d =>
        {
            d.Floor.Outline = Pts((-34, -15.2), (20, -15.2), (42, 0), (42, 15.2), (-34, 15.2));
            d.Floor.Gate = new LayoutPoint { X = 31, Z = -7.6 };
        });
        Assert.Empty(Issues(slanted, "gate-invalid"));
    }

    [Fact]
    public void Gate_invalid_for_an_old_document_follows_the_implicit_gate()
    {
        // 舊文件沒有 Gate：最西邊的牆太短時，隱含的大門就放不下。
        var document = DefaultLayout.Build();
        document.Floor.Outline = Pts((-34, -15.2), (42, -15.2), (42, 15.2), (-34, 15.2), (-34, -8));
        var result = LayoutValidator.Validate(document);
        Assert.DoesNotContain(result.Issues, i => i.Code == "gate-invalid" && i.WallIndices.Count == 0);
    }

    [Fact]
    public void Module_outside_is_polygon_based()
    {
        // 切掉東南角（X 大、Z 小）：找出原本占地碰到缺角的模組。
        var notch = new LayoutRect(20, 42, -15.2, -6);
        var document = Explicit(d => d.Floor.Outline = Pts((-34, -15.2), (20, -15.2), (20, -6), (42, -6), (42, 15.2), (-34, 15.2)));
        var expected = document.Modules
            .Where(m => LayoutGeometry.RectsOverlap(LayoutGeometry.Footprint(m), notch))
            .Select(m => m.Code)
            .OrderBy(c => c)
            .ToList();
        var outside = Issues(document, "module-outside");
        var codes = outside.SelectMany(i => i.ModuleCodes).OrderBy(c => c).ToList();
        Assert.Equal(expected, codes);
        Assert.NotEmpty(codes);

        // 缺角的地方沒有模組時，不會報。
        var corner = Explicit(d => d.Floor.Outline = Pts((-34, -15.2), (42, -15.2), (42, 15.2), (-34, 15.2)));
        Assert.Empty(Issues(corner, "module-outside"));
    }

    [Fact]
    public void Module_outside_old_rectangle_still_works()
    {
        var document = DefaultLayout.Build();
        document.Floor.MinX = -20;
        Assert.NotEmpty(Issues(document, "module-outside"));
    }

    [Fact]
    public void Edge_outside_flags_roads_outside_or_too_close_to_a_wall()
    {
        static FactoryLayoutDocument WithRoad(double ax, double az, double bx, double bz) => Explicit(d =>
        {
            d.RoadNodes.Add(new LayoutRoadNode { Code = "t1", X = ax, Z = az });
            d.RoadNodes.Add(new LayoutRoadNode { Code = "t2", X = bx, Z = bz });
            d.RoadEdges.Add(new LayoutRoadEdge { From = "t1", To = "t2" });
        });

        static bool Flagged(FactoryLayoutDocument document) =>
            Issues(document, "edge-outside").Any(i => i.Edges.Any(e => e.From == "t1" && e.To == "t2"));

        Assert.False(Flagged(WithRoad(30, 14.4, 33, 14.4)), "剛好 0.8 m");
        Assert.True(Flagged(WithRoad(30, 14.8, 33, 14.8)), "離牆 0.4 m");
        Assert.True(Flagged(WithRoad(30, 16, 33, 16)), "在廠房外");
        Assert.True(Flagged(WithRoad(40, 0, 43, 0)), "穿出東牆");
        Assert.False(Flagged(WithRoad(-32, 12, -29, 12)));
        // 沒加道路時預設路網沒有 edge-outside。
        Assert.Empty(Issues(Explicit(), "edge-outside"));
    }

    [Fact]
    public void Edge_outside_uses_the_polygon_not_the_bounding_box()
    {
        var document = Explicit(d =>
        {
            d.Floor.Outline = Pts((-34, -15.2), (42, -15.2), (42, 0), (30, 0), (30, 15.2), (-34, 15.2));
            d.RoadNodes.Add(new LayoutRoadNode { Code = "t1", X = 36, Z = 6 }); // 缺角裡（原本在矩形內）
            d.RoadNodes.Add(new LayoutRoadNode { Code = "t2", X = 36, Z = 10 });
            d.RoadEdges.Add(new LayoutRoadEdge { From = "t1", To = "t2" });
        });
        Assert.Contains(Issues(document, "edge-outside"), i => i.Edges.Any(e => e.From == "t1"));
    }

    [Fact]
    public void Polygon_checks_are_skipped_while_the_floor_itself_is_invalid()
    {
        var document = Explicit(d => d.Floor.Outline = Pts((-34, -15.2), (42, 15.2), (42, -15.2), (-34, 15.2)));
        var result = LayoutValidator.Validate(document);
        Assert.Contains(result.Issues, i => i.Code == "floor-invalid");
        Assert.DoesNotContain(result.Issues, i => i.Code == "module-outside" || i.Code == "edge-outside" || i.Code == "gate-invalid");
    }

    [Fact]
    public void Structural_errors_cover_null_outline_items_and_non_finite_coordinates()
    {
        var nullItem = Explicit(d => d.Floor.Outline![2] = null!);
        var result = LayoutValidator.Validate(nullItem);
        Assert.Single(result.Issues);
        Assert.Equal(LayoutValidator.DocumentInvalid, result.Issues[0].Code);

        foreach (var change in new Action<FactoryLayoutDocument>[]
        {
            d => d.Floor.Outline![0].X = double.NaN,
            d => d.Floor.Outline![0].Z = double.PositiveInfinity,
            d => d.Floor.Gate!.X = double.NaN,
            d => d.Floor.MinX = double.NegativeInfinity,
            d => d.Modules[0].X = double.NaN,
            d => d.RoadNodes[0].Z = double.NaN,
        })
        {
            var broken = LayoutValidator.Validate(Explicit(change));
            Assert.False(broken.IsValid);
            Assert.Single(broken.Issues);
            Assert.Equal(LayoutValidator.DocumentInvalid, broken.Issues[0].Code);
        }
    }

    [Fact]
    public void Issue_marks_collect_error_wall_indices()
    {
        var document = Explicit(d => (d.Floor.Outline![1], d.Floor.Outline[2]) = (d.Floor.Outline[2], d.Floor.Outline[1]));
        var marks = LayoutIssueMarks.From(LayoutValidator.Validate(document));
        Assert.Equal(new[] { 0, 2 }, marks.ErrorWallIndices.OrderBy(i => i).ToArray());
        Assert.Empty(LayoutIssueMarks.From(LayoutValidator.Validate(DefaultLayout.Build())).ErrorWallIndices);
        Assert.Empty(LayoutIssueMarks.From(null).ErrorWallIndices);
    }

    // ------------------------------------------------------------------ 編輯

    [Fact]
    public void First_edit_materializes_the_rectangle_into_outline_and_gate()
    {
        var session = NewSession();
        Assert.Null(session.Document.Floor.Outline);
        Assert.Equal(4, session.FloorWallCount);

        Assert.True(session.MoveFloorVertex(2, 44, 16));

        var floor = session.Document.Floor;
        Assert.NotNull(floor.Outline);
        Assert.Equal(4, floor.Outline.Count);
        Near((44, 16), floor.Outline[2]);
        Near((-34, -15.2), floor.Outline[0]);
        Assert.NotNull(floor.Gate);
        Near((-34, 0), floor.Gate);
        Assert.Equal((-34.0, 44.0, -15.2, 16.0), (floor.MinX, floor.MaxX, floor.MinZ, floor.MaxZ));
    }

    [Fact]
    public void Move_vertex_snaps_to_the_grid_and_is_one_undo_step()
    {
        var session = NewSession();
        var before = Canonical(session);

        Assert.True(session.MoveFloorVertex(1, 44.09, -17.31));
        Near((44, -17.4), session.Document.Floor.Outline![1]);

        Assert.True(session.CanUndo);
        Assert.True(session.Undo());
        Assert.Equal(before, Canonical(session));
        Assert.False(session.CanUndo);
        Assert.True(session.Redo());
        Near((44, -17.4), session.Document.Floor.Outline![1]);
    }

    [Fact]
    public void Move_vertex_no_op_and_bad_input_leave_no_history_and_do_not_materialize()
    {
        var session = NewSession();
        var before = Canonical(session);

        Assert.False(session.MoveFloorVertex(1, 42.05, -15.2)); // 對齊後沒變
        Assert.False(session.MoveFloorVertex(-1, 0, 0));
        Assert.False(session.MoveFloorVertex(4, 0, 0));
        Assert.False(session.MoveFloorVertex(0, double.NaN, 0));
        Assert.False(session.MoveFloorVertex(0, 0, double.PositiveInfinity));

        Assert.False(session.CanUndo);
        Assert.Equal(before, Canonical(session));
        Assert.Null(session.Document.Floor.Outline);
        Assert.Null(session.Document.Floor.Gate);
    }

    [Fact]
    public void Move_vertex_keeps_the_gate_at_the_same_relative_spot_on_its_wall()
    {
        var session = NewSession(); // 大門在西牆 (-34, 0)，西牆是第 3 段：(-34,15.2)→(-34,-15.2)
        // 西牆的終點是頂點 0；把它往南移 -10.2 → -25.4：牆長 40.6，門在中點 → (-34, -5.1)... 相對位置 0.5 不變
        Assert.True(session.MoveFloorVertex(0, -34, -25.4));
        var gate = session.Document.Floor.Gate!;
        Assert.Equal(-34, gate.X, 6);
        Assert.Equal((15.2 + -25.4) / 2, gate.Z, 6);
        Assert.Equal(3, session.GateWallIndex);
        Assert.DoesNotContain(Errors(session), i => i.Code == "gate-invalid");
    }

    private static IEnumerable<LayoutIssueDto> Errors(LayoutEditSession session) =>
        session.Validate().Issues.Where(i => i.Severity == LayoutIssueSeverity.Error);

    [Fact]
    public void Move_vertex_does_not_touch_a_gate_on_a_far_wall()
    {
        var session = NewSession();
        Assert.True(session.MoveFloorVertex(2, 44, 18));
        Near((-34, 0), session.Document.Floor.Gate);
    }

    [Fact]
    public void Move_wall_translates_along_the_normal_and_stretches_the_neighbours()
    {
        var session = NewSession();
        // 第 1 段是東牆 (42,-15.2)→(42,15.2)；往東拖 4.1 m（斜著拖也只取法線方向的分量）。
        Assert.True(session.MoveFloorWall(1, 4.1, 7));

        var outline = session.Document.Floor.Outline!;
        Near((46, -15.2), outline[1]);
        Near((46, 15.2), outline[2]);
        Near((-34, -15.2), outline[0]);
        Near((-34, 15.2), outline[3]);
        Assert.Equal(46, session.Document.Floor.MaxX);
        Assert.Equal(4, outline.Count);
        Assert.Equal(3, session.GateWallIndex);
    }

    [Fact]
    public void Move_wall_works_on_a_slanted_wall_and_no_op_when_the_offset_snaps_to_zero()
    {
        var session = NewSession();
        Assert.True(session.MoveFloorVertex(2, 30, 15.2));
        Assert.True(session.MoveFloorVertex(1, 42, 4.8));
        // 第 1 段 (42,4.8)... 整形成斜牆後的法線方向平移
        var outline = session.Document.Floor.Outline!;
        var a = (outline[1].X, outline[1].Z);
        var b = (outline[2].X, outline[2].Z);
        var length = LayoutGeometry.Distance(a, b);
        var drag = (X: (b.Z - a.Z) / length * 3, Z: -(b.X - a.X) / length * 3); // 沿法線 3 m → 對齊 3.0

        var before = Canonical(session);
        Assert.False(session.MoveFloorWall(1, 0.05, 0.05));
        Assert.Equal(before, Canonical(session));

        Assert.True(session.MoveFloorWall(1, drag.X, drag.Z));
        var moved = session.Document.Floor.Outline!;
        // 兩端各沿法線移動 3 m（取到 0.01 m）
        Assert.Equal(3.0, LayoutGeometry.Distance((outline[1].X, outline[1].Z), (moved[1].X, moved[1].Z)), 1);
        Assert.Equal(length, LayoutGeometry.Distance((moved[1].X, moved[1].Z), (moved[2].X, moved[2].Z)), 1);
    }

    [Fact]
    public void Move_wall_with_the_gate_on_it_carries_the_gate_along()
    {
        var session = NewSession();
        Assert.True(session.MoveFloorWall(3, -6, 0)); // 西牆往西 6 m
        Near((-40, 0), session.Document.Floor.Gate);
        Assert.Equal(3, session.GateWallIndex);
        Assert.Equal(-40, session.Document.Floor.MinX);
        Assert.DoesNotContain(Errors(session), i => i.Code == "gate-invalid");

        Assert.True(session.Undo());
        Near((-34, 0), session.GateCenter());
    }

    [Fact]
    public void Move_wall_rejects_bad_input()
    {
        var session = NewSession();
        Assert.False(session.MoveFloorWall(-1, 5, 0));
        Assert.False(session.MoveFloorWall(4, 5, 0));
        Assert.False(session.MoveFloorWall(0, double.NaN, 0));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void Insert_vertex_splits_the_wall_and_projects_and_snaps()
    {
        var session = NewSession();
        // 第 0 段是南牆 (-34,-15.2)→(42,-15.2)，點在牆外側 (10.07, -20) 會投影成 (10.07, -15.2)，沿牆對齊到 0.2 m。
        Assert.True(session.InsertFloorVertex(0, 10.07, -20));

        var outline = session.Document.Floor.Outline!;
        Assert.Equal(5, outline.Count);
        Near((10.0, -15.2), outline[1]);
        Near((42, -15.2), outline[2]);
        Assert.Equal(5, session.FloorWallCount);
        Assert.Equal(4, session.GateWallIndex); // 西牆現在是第 4 段
        Near((-34, 0), session.Document.Floor.Gate);

        Assert.True(session.Undo());
        Assert.Null(session.Document.Floor.Outline);
    }

    [Fact]
    public void Insert_vertex_on_the_last_wall_appends_and_rejects_ends_and_bad_input()
    {
        var session = NewSession();
        Assert.True(session.InsertFloorVertex(3, -34, 5));
        Assert.Equal(5, session.Document.Floor.Outline!.Count);
        Near((-34, 5), session.Document.Floor.Outline[4]);
        Near((-34, 0), session.Document.Floor.Gate);

        var second = NewSession();
        var before = Canonical(second);
        Assert.False(second.InsertFloorVertex(0, -34.05, -15.2));  // 對齊後落在牆的起點
        Assert.False(second.InsertFloorVertex(0, 99, -15.2));      // 投影在終點
        Assert.False(second.InsertFloorVertex(-1, 0, 0));
        Assert.False(second.InsertFloorVertex(4, 0, 0));
        Assert.False(second.InsertFloorVertex(0, double.NaN, 0));
        Assert.Equal(before, Canonical(second));
        Assert.False(second.CanUndo);
    }

    [Fact]
    public void Insert_vertex_stops_at_the_vertex_limit()
    {
        var session = NewSession();
        while (session.CanInsertFloorVertex)
        {
            // 在最後一段（西牆，往南走）離起點 0.4 m 的地方加轉角；剩下的那段仍是最後一段。
            var outline = session.FloorOutline();
            var last = outline[^1];
            Assert.True(session.InsertFloorVertex(outline.Count - 1, last.X, last.Z - 0.4));
        }

        Assert.Equal(LayoutRules.MaxFloorVertices, session.FloorWallCount);
        Assert.False(session.InsertFloorVertex(0, 0, -15.2));
    }

    [Fact]
    public void Remove_vertex_merges_the_walls_and_keeps_at_least_three()
    {
        var session = NewSession();
        Assert.True(session.InsertFloorVertex(0, 10, -15.2));
        Assert.Equal(5, session.FloorWallCount);
        Assert.True(session.CanRemoveFloorVertex);

        Assert.True(session.RemoveFloorVertex(1));
        Assert.Equal(4, session.FloorWallCount);
        Assert.True(session.RemoveFloorVertex(3));
        Assert.Equal(3, session.FloorWallCount);
        Assert.False(session.CanRemoveFloorVertex);

        var before = Canonical(session);
        Assert.False(session.RemoveFloorVertex(0));
        Assert.False(session.RemoveFloorVertex(-1));
        Assert.Equal(before, Canonical(session));
        Assert.Equal(3, session.Document.Floor.Outline!.Count);

        Assert.True(session.Undo());
        Assert.Equal(4, session.Document.Floor.Outline!.Count);
    }

    [Fact]
    public void Remove_vertex_projects_a_gate_whose_wall_disappeared()
    {
        var session = NewSession(); // 大門在西牆（第 3 段，頂點 3→0）；刪頂點 0，西牆併進南牆：(-34,15.2)→(42,-15.2)
        Assert.True(session.RemoveFloorVertex(0));
        var outline = session.Document.Floor.Outline!;
        Assert.Equal(3, outline.Count);
        var gate = session.Document.Floor.Gate!;
        var hit = FloorGeometry.NearestWall(outline, gate.X, gate.Z)!;
        Assert.True(hit.Distance <= 0.02, "大門仍在牆上");
        Assert.NotEqual(-1, session.GateWallIndex);
    }

    [Fact]
    public void Remove_vertex_keeps_a_gate_on_a_surviving_wall()
    {
        var session = NewSession();
        Assert.True(session.RemoveFloorVertex(1)); // 東南角：南牆與東牆併掉；西牆不受影響
        Near((-34, 0), session.Document.Floor.Gate);
    }

    [Fact]
    public void Move_gate_projects_to_the_nearest_wall_and_snaps_along_it()
    {
        var session = NewSession();
        Assert.True(session.MoveGate(10.07, -30)); // 離南牆最近
        var gate = session.Document.Floor.Gate!;
        Assert.Equal(-15.2, gate.Z, 6);
        // 沿牆從起點 (-34) 量起對齊 0.2 m：10.07 - (-34) = 44.07 → 44.0 → x = 10
        Assert.Equal(10, gate.X, 6);
        Assert.Equal(0, session.GateWallIndex);
        Assert.DoesNotContain(Errors(session), i => i.Code == "gate-invalid");

        Assert.True(session.Undo());
        Assert.Null(session.Document.Floor.Gate);
        Near((-34, 0), session.GateCenter());
    }

    [Fact]
    public void Move_gate_no_op_does_not_materialize_and_bad_input_fails()
    {
        var session = NewSession();
        Assert.False(session.MoveGate(-40, 0.04));
        Assert.False(session.CanUndo);
        Assert.Null(session.Document.Floor.Outline);
        Assert.Null(session.Document.Floor.Gate);
        Assert.False(session.MoveGate(double.NaN, 0));
        Assert.False(session.MoveGate(0, double.NegativeInfinity));
    }

    [Fact]
    public void Move_gate_onto_a_short_or_slanted_wall_is_reported_by_validation()
    {
        var session = NewSession();
        Assert.True(session.MoveFloorVertex(1, 24, -15.2)); // 南牆縮成 58 m，右邊變斜牆 (24,-15.2)→(42,15.2)
        Assert.True(session.MoveGate(33, 0));
        Assert.Equal(1, session.GateWallIndex);
        Assert.DoesNotContain(Errors(session), i => i.Code == "gate-invalid"); // 斜牆夠長

        Assert.True(session.MoveFloorVertex(2, 25, -10));    // 讓斜牆變短（約 5 m）
        Assert.True(session.MoveGate(24.6, -12));
        var gateIssues = Errors(session).Where(i => i.Code == "gate-invalid").ToList();
        Assert.Single(gateIssues);
        Assert.Contains(session.GateWallIndex, gateIssues[0].WallIndices);
    }

    [Fact]
    public void Each_successful_operation_is_one_undo_step_and_new_operations_clear_redo()
    {
        var session = NewSession();
        var start = Canonical(session);
        Assert.True(session.MoveFloorVertex(2, 44, 16));
        Assert.True(session.MoveFloorWall(1, 2, 0));
        Assert.True(session.InsertFloorVertex(0, 0, -15.2));
        Assert.True(session.MoveGate(0, -15.2));
        Assert.True(session.RemoveFloorVertex(1));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(session.Undo(), $"undo {i}");
        }

        Assert.Equal(start, Canonical(session));
        Assert.False(session.CanUndo);
        Assert.True(session.Redo());
        Assert.True(session.CanRedo);
        Assert.True(session.MoveFloorVertex(0, -36, -16));
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void Floor_edits_make_the_session_dirty_and_undo_restores_the_saved_state()
    {
        var session = NewSession();
        Assert.False(session.IsDirty);
        Assert.True(session.MoveFloorVertex(2, 44, 16));
        Assert.True(session.IsDirty);
        session.Undo();
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Floor_edits_do_not_move_modules_or_roads_and_validation_reports_the_overflow()
    {
        var session = NewSession();
        var modules = session.Document.Modules.Select(m => (m.Code, m.X, m.Z)).ToList();
        var edges = session.Document.RoadEdges.Count;
        Assert.True(session.MoveFloorWall(1, -20, 0)); // 東牆往西 20 m
        Assert.Equal(modules, session.Document.Modules.Select(m => (m.Code, m.X, m.Z)).ToList());
        Assert.Equal(edges, session.Document.RoadEdges.Count);
        Assert.Contains(session.Validate().Issues, i => i.Code == "module-outside");
        Assert.Contains(session.Validate().Issues, i => i.Code == "edge-outside");
    }

    [Fact]
    public void Set_floor_size_with_an_outline_scales_the_vertices_about_the_centre_and_the_gate_follows()
    {
        var session = NewSession(); // 外框 76 × 30.4，中心 (4, 0)
        Assert.True(session.InsertFloorVertex(0, 4, -15.2)); // 材質化並加轉角 (4,-15.2)
        var gateBefore = session.Document.Floor.Gate!;

        Assert.True(session.SetFloorSize(40, 30.4));

        var floor = session.Document.Floor;
        Assert.Equal(40.0, floor.MaxX - floor.MinX, 6);
        Assert.Equal(30.4, floor.MaxZ - floor.MinZ, 6);
        Assert.Equal(4.0, (floor.MinX + floor.MaxX) / 2, 6);
        Assert.Equal(5, floor.Outline!.Count);
        Near((4, -15.2), floor.Outline[1]); // 中心線上的點不動
        Near((-16, -15.2), floor.Outline[0]);
        Near((-16, 0), floor.Gate);
        Assert.Equal(3 + 1, session.GateWallIndex);
        Assert.NotNull(gateBefore);

        Assert.False(session.SetFloorSize(40, 30.4));
    }

    [Fact]
    public void Set_floor_size_on_a_rectangle_keeps_the_outline_null()
    {
        var session = NewSession();
        Assert.True(session.SetFloorSize(80, 30.4));
        Assert.Null(session.Document.Floor.Outline);
        Assert.Null(session.Document.Floor.Gate);
        Near((-36, 0), session.GateCenter());
    }

    [Fact]
    public void Set_floor_size_drag_is_still_a_single_undo_step_with_an_outline()
    {
        var session = NewSession();
        Assert.True(session.MoveFloorVertex(2, 44, 16));
        var afterMove = Canonical(session);

        Assert.True(session.SetFloorSize(70, 30));
        Assert.True(session.SetFloorSize(72, 30));
        Assert.True(session.SetFloorSize(74, 30));
        Assert.True(session.Undo());
        Assert.Equal(afterMove, Canonical(session));
    }

    [Fact]
    public void Can_place_uses_the_polygon()
    {
        // 沒有其他模組，只看占地是否在多邊形內：切掉東南角。
        var document = DefaultLayout.Build();
        document.Modules.Clear();
        var session = new LayoutEditSession(document);
        Assert.True(session.CanPlace(ModuleKind.MiscStore, 36, -10, 0, null));

        Assert.True(session.MoveFloorVertex(1, 20, -15.2));
        Assert.True(session.CanPlace(ModuleKind.MiscStore, 0, 0, 0, null));
        Assert.False(session.CanPlace(ModuleKind.MiscStore, 36, -10, 0, null), "缺角裡");
        Assert.False(session.CanPlace(ModuleKind.MiscStore, 60, 0, 0, null));
    }

    [Fact]
    public void Read_helpers_describe_the_floor()
    {
        var session = NewSession();
        Assert.Equal(4, session.FloorWallCount);
        Assert.Equal(76 * 30.4, session.FloorArea, 6);
        Assert.Equal(3, session.GateWallIndex);
        Assert.True(session.CanInsertFloorVertex);
        Assert.True(session.CanRemoveFloorVertex);
        Assert.Equal(4, session.FloorOutline().Count);
        Near((-34, 0), session.GateCenter());
    }

    [Fact]
    public void Floor_outline_helpers_return_copies()
    {
        var session = NewSession();
        session.FloorOutline()[0].X = 999;
        Assert.Null(session.Document.Floor.Outline);
        Assert.True(session.MoveFloorVertex(2, 44, 16));
        session.FloorOutline()[0].X = 999;
        Assert.Equal(-34, session.Document.Floor.Outline![0].X);
    }
}
