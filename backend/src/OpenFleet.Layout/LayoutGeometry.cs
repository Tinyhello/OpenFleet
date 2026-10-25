using System;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>平面上的軸對齊矩形（公尺）。</summary>
    public sealed class LayoutRect
    {
        public LayoutRect(double minX, double maxX, double minZ, double maxZ)
        {
            MinX = minX;
            MaxX = maxX;
            MinZ = minZ;
            MaxZ = maxZ;
        }

        public double MinX { get; }
        public double MaxX { get; }
        public double MinZ { get; }
        public double MaxZ { get; }

        /// <summary>四邊各往內縮 <paramref name="inset"/> 後的矩形。</summary>
        public LayoutRect Shrink(double inset) => new LayoutRect(MinX + inset, MaxX - inset, MinZ + inset, MaxZ - inset);
    }

    /// <summary>
    /// 佈局的幾何計算：旋轉、交接點、占地，以及路段的相交與距離。
    /// 座標單位是公尺；X 向右、Z 向南（朝走道的那側是 +Z）。
    /// </summary>
    public static class LayoutGeometry
    {
        // 數值的誤差歸零：cos(90°) 這類算出來的 6e-17 會讓相等比較與代碼產生變得不穩定。
        private const double Epsilon = 1e-9;

        /// <summary>
        /// 繞原點旋轉。θ = rotation 度，從上往下看順時針（和 Unity 的 yaw 相同）：
        /// x' = x·cosθ + z·sinθ，z' = −x·sinθ + z·cosθ。
        /// </summary>
        public static (double X, double Z) Rotate(double x, double z, int rotation)
        {
            var radians = rotation * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            return (Snap(x * cos + z * sin), Snap(-x * sin + z * cos));
        }

        /// <summary>模組的交接點（世界座標）；沒有交接點的種類回傳 null。</summary>
        public static (double X, double Z)? HandoffPoint(LayoutModule module)
        {
            var spec = ModuleCatalog.Get(module.Kind);
            if (!spec.HasHandoff)
            {
                return null;
            }

            var (x, z) = Rotate(spec.HandoffX, spec.HandoffZ, module.Rotation);
            return (Clean(module.X + x), Clean(module.Z + z));
        }

        /// <summary>從交接點往外接路的方向（單位向量）。</summary>
        public static (double X, double Z) FacingDirection(LayoutModule module) => Rotate(0, 1, module.Rotation);

        /// <summary>模組的占地（世界座標的軸對齊矩形）。90、270 度時寬與深互換，中心偏移也跟著旋轉。</summary>
        public static LayoutRect Footprint(LayoutModule module)
        {
            var spec = ModuleCatalog.Get(module.Kind);
            var swap = module.Rotation == 90 || module.Rotation == 270;
            var width = swap ? spec.Depth : spec.Width;
            var depth = swap ? spec.Width : spec.Depth;
            var (offsetX, offsetZ) = Rotate(spec.OffsetX, spec.OffsetZ, module.Rotation);
            var centerX = module.X + offsetX;
            var centerZ = module.Z + offsetZ;
            return new LayoutRect(
                Clean(centerX - width / 2),
                Clean(centerX + width / 2),
                Clean(centerZ - depth / 2),
                Clean(centerZ + depth / 2));
        }

        /// <summary>兩個矩形的重疊面積超過 <see cref="LayoutRules.OverlapArea"/> 才算重疊；貼邊不算。</summary>
        public static bool RectsOverlap(LayoutRect a, LayoutRect b)
        {
            var width = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            var depth = Math.Min(a.MaxZ, b.MaxZ) - Math.Max(a.MinZ, b.MinZ);
            return width > 0 && depth > 0 && width * depth > LayoutRules.OverlapArea;
        }

        /// <summary>內側矩形完全在外側之內（容許 <see cref="LayoutRules.InsideTolerance"/>）。</summary>
        public static bool RectInside(LayoutRect inner, LayoutRect outer)
        {
            var tolerance = LayoutRules.InsideTolerance;
            return inner.MinX >= outer.MinX - tolerance
                && inner.MaxX <= outer.MaxX + tolerance
                && inner.MinZ >= outer.MinZ - tolerance
                && inner.MaxZ <= outer.MaxZ + tolerance;
        }

        /// <summary>線段是否穿過矩形的內部（只碰到邊界不算），用 Liang–Barsky 裁切。</summary>
        public static bool SegmentIntersectsRect((double X, double Z) a, (double X, double Z) b, LayoutRect rect)
        {
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            double t0 = 0;
            double t1 = 1;
            if (!Clip(-dx, a.X - rect.MinX, ref t0, ref t1)
                || !Clip(dx, rect.MaxX - a.X, ref t0, ref t1)
                || !Clip(-dz, a.Z - rect.MinZ, ref t0, ref t1)
                || !Clip(dz, rect.MaxZ - a.Z, ref t0, ref t1))
            {
                return false;
            }

            return t1 - t0 > 1e-9;
        }

        /// <summary>點到線段的最短距離。</summary>
        public static double PointSegmentDistance((double X, double Z) p, (double X, double Z) a, (double X, double Z) b)
        {
            var abX = b.X - a.X;
            var abZ = b.Z - a.Z;
            var lengthSquared = abX * abX + abZ * abZ;
            if (lengthSquared <= 0)
            {
                return Distance(p, a);
            }

            var t = ((p.X - a.X) * abX + (p.Z - a.Z) * abZ) / lengthSquared;
            t = Math.Max(0, Math.Min(1, t));
            return Distance(p, (a.X + t * abX, a.Z + t * abZ));
        }

        /// <summary>兩條線段的最短距離；相交（含碰到）時為 0。</summary>
        public static double SegmentDistance((double X, double Z) a1, (double X, double Z) a2, (double X, double Z) b1, (double X, double Z) b2)
        {
            if (SegmentsIntersect(a1, a2, b1, b2))
            {
                return 0;
            }

            return Math.Min(
                Math.Min(PointSegmentDistance(a1, b1, b2), PointSegmentDistance(a2, b1, b2)),
                Math.Min(PointSegmentDistance(b1, a1, a2), PointSegmentDistance(b2, a1, a2)));
        }

        /// <summary>
        /// 兩條路段在中間交叉或碰到（T 字形也算）。共用端點的路段不算：路口本來就是節點。
        /// </summary>
        public static bool SegmentsCross((double X, double Z) a1, (double X, double Z) a2, (double X, double Z) b1, (double X, double Z) b2)
        {
            if (SamePoint(a1, b1) || SamePoint(a1, b2) || SamePoint(a2, b1) || SamePoint(a2, b2))
            {
                return false;
            }

            return SegmentsIntersect(a1, a2, b1, b2);
        }

        /// <summary>兩點的距離（公尺）。</summary>
        public static double Distance((double X, double Z) a, (double X, double Z) b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        /// <summary>兩點是否在容許誤差內是同一點（交接點與節點的比對用 <see cref="LayoutRules.HandoffMatch"/>）。</summary>
        public static bool SamePoint((double X, double Z) a, (double X, double Z) b, double tolerance = 1e-6) => Distance(a, b) <= tolerance;

        private static double Snap(double value) => Math.Abs(value) < Epsilon ? 0 : value;

        // 座標是公分級的數字（2 位小數），加減之後留下的 1e-16 誤差會讓代碼與相等比較不穩定；取到 9 位小數就回到原本的數字。
        private static double Clean(double value) => Math.Round(value, 9);

        private static bool Clip(double p, double q, ref double t0, ref double t1)
        {
            if (Math.Abs(p) < Epsilon)
            {
                return q >= 0;
            }

            var t = q / p;
            if (p < 0)
            {
                if (t > t1)
                {
                    return false;
                }

                if (t > t0)
                {
                    t0 = t;
                }
            }
            else
            {
                if (t < t0)
                {
                    return false;
                }

                if (t < t1)
                {
                    t1 = t;
                }
            }

            return true;
        }

        internal static bool SegmentsIntersect((double X, double Z) p1, (double X, double Z) p2, (double X, double Z) p3, (double X, double Z) p4)
        {
            var d1 = Cross(p3, p4, p1);
            var d2 = Cross(p3, p4, p2);
            var d3 = Cross(p1, p2, p3);
            var d4 = Cross(p1, p2, p4);

            if (((d1 > Epsilon && d2 < -Epsilon) || (d1 < -Epsilon && d2 > Epsilon))
                && ((d3 > Epsilon && d4 < -Epsilon) || (d3 < -Epsilon && d4 > Epsilon)))
            {
                return true;
            }

            return (Math.Abs(d1) <= Epsilon && OnSegment(p3, p4, p1))
                || (Math.Abs(d2) <= Epsilon && OnSegment(p3, p4, p2))
                || (Math.Abs(d3) <= Epsilon && OnSegment(p1, p2, p3))
                || (Math.Abs(d4) <= Epsilon && OnSegment(p1, p2, p4));
        }

        private static double Cross((double X, double Z) a, (double X, double Z) b, (double X, double Z) c) =>
            (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);

        // 假設 p 已經和 a-b 共線，只檢查 p 是否落在兩端之間。
        private static bool OnSegment((double X, double Z) a, (double X, double Z) b, (double X, double Z) p) =>
            p.X >= Math.Min(a.X, b.X) - Epsilon && p.X <= Math.Max(a.X, b.X) + Epsilon
            && p.Z >= Math.Min(a.Z, b.Z) - Epsilon && p.Z <= Math.Max(a.Z, b.Z) + Epsilon;
    }
}
