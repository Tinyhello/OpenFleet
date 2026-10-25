using System;
using System.Collections.Generic;
using System.Linq;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>某點在外牆上的最近點。</summary>
    public sealed class WallHit
    {
        /// <summary>牆的編號（第 i 段牆是 Outline[i]→Outline[i+1]）。</summary>
        public int Index { get; set; }

        /// <summary>點到這段牆的距離（公尺）。</summary>
        public double Distance { get; set; }

        /// <summary>最近點在牆上的位置：0 是起點、1 是終點。</summary>
        public double T { get; set; }

        /// <summary>最近點離牆起點的距離（公尺）。</summary>
        public double Along { get; set; }

        /// <summary>最近點的座標。</summary>
        public double X { get; set; }

        public double Z { get; set; }
    }

    /// <summary>
    /// 廠房多邊形的幾何計算（外牆頂點依序相連、最後一點接回第一點）。座標單位是公尺，X 向右、Z 向南。
    /// 「逆時針」指把 X 當橫軸、Z 當縱軸時的數學方向（有號面積為正），預設矩形的順序
    /// (MinX,MinZ)、(MaxX,MinZ)、(MaxX,MaxZ)、(MinX,MaxZ) 就是逆時針。
    /// 輸入的 outline 至少 3 個頂點且沒有 null 才有意義（驗證器先擋掉結構錯誤）。
    /// </summary>
    public static class FloorGeometry
    {
        private const double Eps = 1e-9;

        /// <summary>有號面積：逆時針為正、順時針為負。</summary>
        public static double SignedArea(IReadOnlyList<LayoutPoint> outline)
        {
            double sum = 0;
            for (var i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                sum += a.X * b.Z - b.X * a.Z;
            }

            return sum / 2;
        }

        /// <summary>面積（平方公尺），不分方向。</summary>
        public static double Area(IReadOnlyList<LayoutPoint> outline) => Math.Abs(SignedArea(outline));

        /// <summary>方向正規化：回傳逆時針順序的複本（原本就是逆時針就照原順序）。</summary>
        public static List<LayoutPoint> CounterClockwise(IReadOnlyList<LayoutPoint> outline)
        {
            var copy = outline.Select(p => new LayoutPoint { X = p.X, Z = p.Z }).ToList();
            if (SignedArea(outline) < 0)
            {
                copy.Reverse();
            }

            return copy;
        }

        /// <summary>外牆的段數（等於頂點數）。</summary>
        public static int WallCount(IReadOnlyList<LayoutPoint> outline) => outline.Count;

        /// <summary>第 <paramref name="wall"/> 段牆的起點。</summary>
        public static (double X, double Z) WallStart(IReadOnlyList<LayoutPoint> outline, int wall) => (outline[wall].X, outline[wall].Z);

        /// <summary>第 <paramref name="wall"/> 段牆的終點（最後一段接回第一個頂點）。</summary>
        public static (double X, double Z) WallEnd(IReadOnlyList<LayoutPoint> outline, int wall)
        {
            var next = outline[(wall + 1) % outline.Count];
            return (next.X, next.Z);
        }

        public static double WallLength(IReadOnlyList<LayoutPoint> outline, int wall) =>
            LayoutGeometry.Distance(WallStart(outline, wall), WallEnd(outline, wall));

        public static (double X, double Z) WallMidpoint(IReadOnlyList<LayoutPoint> outline, int wall) => PointOnWall(outline, wall, 0.5);

        /// <summary>牆上 t 位置的點（0 是起點、1 是終點）。</summary>
        public static (double X, double Z) PointOnWall(IReadOnlyList<LayoutPoint> outline, int wall, double t)
        {
            var a = WallStart(outline, wall);
            var b = WallEnd(outline, wall);
            return (a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);
        }

        /// <summary>牆的方向角（度，0～360）：從起點看向終點，atan2(dz, dx)；0 度朝 +X、90 度朝 +Z（南）。</summary>
        public static double WallAngle(IReadOnlyList<LayoutPoint> outline, int wall)
        {
            var a = WallStart(outline, wall);
            var b = WallEnd(outline, wall);
            var degrees = Math.Atan2(b.Z - a.Z, b.X - a.X) * 180.0 / Math.PI;
            return degrees < 0 ? degrees + 360 : degrees;
        }

        /// <summary>牆朝廠房外的單位法向量（不論 outline 是順時針還是逆時針）；長度為 0 的牆回傳 (0, 0)。</summary>
        public static (double X, double Z) WallOutwardNormal(IReadOnlyList<LayoutPoint> outline, int wall)
        {
            var a = WallStart(outline, wall);
            var b = WallEnd(outline, wall);
            var length = LayoutGeometry.Distance(a, b);
            if (length <= 0)
            {
                return (0, 0);
            }

            // 逆時針時，往前進方向的右手邊是外側：(dz, -dx)。
            var sign = SignedArea(outline) >= 0 ? 1 : -1;
            return (sign * (b.Z - a.Z) / length, sign * -(b.X - a.X) / length);
        }

        /// <summary>離 (x, z) 最近的一段牆；距離相同取編號較小的。沒有牆時回傳 null。</summary>
        public static WallHit? NearestWall(IReadOnlyList<LayoutPoint> outline, double x, double z)
        {
            WallHit? best = null;
            for (var i = 0; i < outline.Count; i++)
            {
                var hit = ProjectOnWall(outline, i, x, z);
                if (best == null || hit.Distance < best.Distance - 1e-12)
                {
                    best = hit;
                }
            }

            return best;
        }

        /// <summary>(x, z) 在第 <paramref name="wall"/> 段牆上的最近點（限制在牆的兩端之間）。</summary>
        public static WallHit ProjectOnWall(IReadOnlyList<LayoutPoint> outline, int wall, double x, double z)
        {
            var a = WallStart(outline, wall);
            var b = WallEnd(outline, wall);
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var lengthSquared = dx * dx + dz * dz;
            var t = lengthSquared <= 0 ? 0 : ((x - a.X) * dx + (z - a.Z) * dz) / lengthSquared;
            t = Math.Max(0, Math.Min(1, t));
            var px = a.X + dx * t;
            var pz = a.Z + dz * t;
            return new WallHit
            {
                Index = wall,
                T = t,
                Along = Math.Sqrt(lengthSquared) * t,
                X = px,
                Z = pz,
                Distance = LayoutGeometry.Distance((x, z), (px, pz)),
            };
        }

        /// <summary>
        /// 點在多邊形內（邊界算在內，離牆 <paramref name="tolerance"/> 以內也算）。
        /// 內部用射線法（往 +X 射出、數穿過幾段牆）。
        /// </summary>
        public static bool PointInPolygon(IReadOnlyList<LayoutPoint> outline, double x, double z, double tolerance = LayoutRules.InsideTolerance)
        {
            var count = outline.Count;
            if (count < 3)
            {
                return false;
            }

            var inside = false;
            for (var i = 0; i < count; i++)
            {
                var a = WallStart(outline, i);
                var b = WallEnd(outline, i);
                if (LayoutGeometry.PointSegmentDistance((x, z), a, b) <= tolerance)
                {
                    return true;
                }

                if ((a.Z > z) != (b.Z > z) && x < (b.X - a.X) * (z - a.Z) / (b.Z - a.Z) + a.X)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>
        /// 矩形完全在多邊形內（含邊界，容許 <see cref="LayoutRules.InsideTolerance"/>）：四個角都在內，
        /// 而且沒有任何牆穿過矩形的內部、也沒有頂點落在矩形內部。矩形只是貼著牆不算穿過。
        /// </summary>
        public static bool RectInsidePolygon(IReadOnlyList<LayoutPoint> outline, LayoutRect rect)
        {
            if (outline.Count < 3)
            {
                return false;
            }

            if (!PointInPolygon(outline, rect.MinX, rect.MinZ) || !PointInPolygon(outline, rect.MaxX, rect.MinZ)
                || !PointInPolygon(outline, rect.MaxX, rect.MaxZ) || !PointInPolygon(outline, rect.MinX, rect.MaxZ))
            {
                return false;
            }

            // 內縮容許誤差：貼著斜牆、差幾公釐的不算穿過。
            var core = rect.Shrink(LayoutRules.InsideTolerance);
            if (core.MinX >= core.MaxX || core.MinZ >= core.MaxZ)
            {
                return true;
            }

            for (var i = 0; i < outline.Count; i++)
            {
                var p = outline[i];
                if (p.X > core.MinX && p.X < core.MaxX && p.Z > core.MinZ && p.Z < core.MaxZ)
                {
                    return false;
                }

                if (LayoutGeometry.SegmentIntersectsRect(WallStart(outline, i), WallEnd(outline, i), core))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 線段完全在多邊形內，而且離每段牆至少 <paramref name="clearance"/>：兩端在內、不和任何牆交叉，
        /// 線段到每段牆的最短距離不小於 clearance（容許 <see cref="LayoutRules.Tolerance"/>）。
        /// clearance 為 0 時只擋「兩端在外」與「穿過牆」。
        /// </summary>
        public static bool SegmentInsidePolygon(IReadOnlyList<LayoutPoint> outline, (double X, double Z) a, (double X, double Z) b, double clearance)
        {
            if (outline.Count < 3 || !PointInPolygon(outline, a.X, a.Z) || !PointInPolygon(outline, b.X, b.Z))
            {
                return false;
            }

            for (var i = 0; i < outline.Count; i++)
            {
                var w1 = WallStart(outline, i);
                var w2 = WallEnd(outline, i);
                if (ProperlyCross(a, b, w1, w2))
                {
                    return false;
                }

                if (clearance > 0 && LayoutGeometry.SegmentDistance(a, b, w1, w2) < clearance - LayoutRules.Tolerance)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 自交的牆編號（升冪）：不相鄰的兩段牆交叉或碰到，或相鄰兩段折回（共線且方向相反、互相重疊）。
        /// 長度為 0 的牆不在這裡報（驗證器另外報重複點）。沒有問題時回傳空清單。
        /// </summary>
        public static List<int> SelfIntersectingWalls(IReadOnlyList<LayoutPoint> outline)
        {
            var count = outline.Count;
            var bad = new SortedSet<int>();
            for (var i = 0; i < count; i++)
            {
                var a1 = WallStart(outline, i);
                var a2 = WallEnd(outline, i);
                if (LayoutGeometry.SamePoint(a1, a2))
                {
                    continue;
                }

                for (var j = i + 1; j < count; j++)
                {
                    var b1 = WallStart(outline, j);
                    var b2 = WallEnd(outline, j);
                    if (LayoutGeometry.SamePoint(b1, b2))
                    {
                        continue;
                    }

                    var adjacent = j == i + 1 || (i == 0 && j == count - 1);
                    if (adjacent)
                    {
                        if (FoldsBack(a1, a2, b1, b2, j == i + 1))
                        {
                            bad.Add(i);
                            bad.Add(j);
                        }
                    }
                    else if (LayoutGeometry.SegmentsIntersect(a1, a2, b1, b2))
                    {
                        bad.Add(i);
                        bad.Add(j);
                    }
                }
            }

            return bad.ToList();
        }

        /// <summary>多邊形沒有自交（含折回）。</summary>
        public static bool IsSimple(IReadOnlyList<LayoutPoint> outline) => SelfIntersectingWalls(outline).Count == 0;

        /// <summary>
        /// 耳切法三角化（凹多邊形也正確）。回傳攤平的索引陣列，每 3 個一組、索引指向 outline 的頂點；
        /// 所有三角形的順序都是逆時針（有號面積為正），不論輸入是順時針還是逆時針。
        /// Unity 的正面是順時針（俯視、X 向右 Z 向上），建網格時每組要把後兩個索引對調。
        /// 輸入要是簡單多邊形（沒有自交）；退化（共線頂點）時會略過那個頂點，三角形面積總和仍等於多邊形面積。
        /// 少於 3 個頂點回傳空陣列。
        /// </summary>
        public static int[] Triangulate(IReadOnlyList<LayoutPoint> outline)
        {
            var count = outline.Count;
            if (count < 3)
            {
                return new int[0];
            }

            var remaining = Enumerable.Range(0, count).ToList();
            if (SignedArea(outline) < 0)
            {
                remaining.Reverse();
            }

            var result = new List<int>();
            while (remaining.Count > 3)
            {
                var clipped = false;
                for (var i = 0; i < remaining.Count; i++)
                {
                    var prev = remaining[(i + remaining.Count - 1) % remaining.Count];
                    var cur = remaining[i];
                    var next = remaining[(i + 1) % remaining.Count];
                    if (Cross(outline[prev], outline[cur], outline[next]) <= Eps || ContainsOther(outline, remaining, prev, cur, next))
                    {
                        continue;
                    }

                    result.Add(prev);
                    result.Add(cur);
                    result.Add(next);
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }

                if (clipped)
                {
                    continue;
                }

                // 找不到耳朵：多半是共線的頂點擋住對角線。拿掉共線頂點（不改變形狀）；真的沒有就放棄剩下的部分。
                var degenerate = -1;
                for (var i = 0; i < remaining.Count && degenerate < 0; i++)
                {
                    var prev = remaining[(i + remaining.Count - 1) % remaining.Count];
                    var next = remaining[(i + 1) % remaining.Count];
                    if (Math.Abs(Cross(outline[prev], outline[remaining[i]], outline[next])) <= Eps)
                    {
                        degenerate = i;
                    }
                }

                if (degenerate < 0)
                {
                    return result.ToArray();
                }

                remaining.RemoveAt(degenerate);
            }

            if (remaining.Count == 3 && Math.Abs(Cross(outline[remaining[0]], outline[remaining[1]], outline[remaining[2]])) > Eps)
            {
                result.AddRange(remaining);
            }

            return result.ToArray();
        }

        private static double Cross(LayoutPoint a, LayoutPoint b, LayoutPoint c) =>
            (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);

        // 三角形（逆時針）prev-cur-next 內是否有別的頂點（含邊上；和三角形頂點同座標的不算）。
        private static bool ContainsOther(IReadOnlyList<LayoutPoint> outline, List<int> remaining, int prev, int cur, int next)
        {
            var a = outline[prev];
            var b = outline[cur];
            var c = outline[next];
            foreach (var k in remaining)
            {
                if (k == prev || k == cur || k == next)
                {
                    continue;
                }

                var p = outline[k];
                if (SameXZ(p, a) || SameXZ(p, b) || SameXZ(p, c))
                {
                    continue;
                }

                if (Cross(a, b, p) >= -Eps && Cross(b, c, p) >= -Eps && Cross(c, a, p) >= -Eps)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameXZ(LayoutPoint a, LayoutPoint b) => Math.Abs(a.X - b.X) <= Eps && Math.Abs(a.Z - b.Z) <= Eps;

        // 相鄰兩段牆（共用一個端點）折回：共線、方向相反，所以兩段重疊在同一條線上。
        // ordered 為 true 表示 a 在 b 之前（共用 a2＝b1）；為 false 表示 b 是最後一段、a 是第一段（共用 b2＝a1）。
        private static bool FoldsBack((double X, double Z) a1, (double X, double Z) a2, (double X, double Z) b1, (double X, double Z) b2, bool ordered)
        {
            // 以共用點為原點，兩段牆各自朝外的向量：相反方向就是折回。
            var shared = ordered ? a2 : a1;
            var u = ordered ? (X: a1.X - shared.X, Z: a1.Z - shared.Z) : (X: a2.X - shared.X, Z: a2.Z - shared.Z);
            var v = ordered ? (X: b2.X - shared.X, Z: b2.Z - shared.Z) : (X: b1.X - shared.X, Z: b1.Z - shared.Z);
            var lu = Math.Sqrt(u.X * u.X + u.Z * u.Z);
            var lv = Math.Sqrt(v.X * v.X + v.Z * v.Z);
            if (lu <= 0 || lv <= 0)
            {
                return false;
            }

            var cross = (u.X * v.Z - u.Z * v.X) / (lu * lv);
            var dot = (u.X * v.X + u.Z * v.Z) / (lu * lv);

            // 兩個朝外向量同方向（dot > 0）就是折回：兩段牆從共用點出發、重疊在同一側。
            return Math.Abs(cross) <= 1e-9 && dot > 0;
        }

        // 兩線段在各自內部真正交叉（碰到端點不算）。
        private static bool ProperlyCross((double X, double Z) p1, (double X, double Z) p2, (double X, double Z) p3, (double X, double Z) p4)
        {
            var d1 = Orient(p3, p4, p1);
            var d2 = Orient(p3, p4, p2);
            var d3 = Orient(p1, p2, p3);
            var d4 = Orient(p1, p2, p4);
            return ((d1 > Eps && d2 < -Eps) || (d1 < -Eps && d2 > Eps)) && ((d3 > Eps && d4 < -Eps) || (d3 < -Eps && d4 > Eps));
        }

        private static double Orient((double X, double Z) a, (double X, double Z) b, (double X, double Z) c) =>
            (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
    }
}
