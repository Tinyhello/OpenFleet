using System;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>吸附到什麼：格點、節點、路段（投影到路段上）。</summary>
    public enum RoadSnapKind
    {
        Grid,
        Node,
        Edge,
    }

    /// <summary>吸附結果：<see cref="X"/>、<see cref="Z"/> 已四捨五入到 0.01 m。</summary>
    public sealed class RoadSnap
    {
        public double X { get; set; }
        public double Z { get; set; }
        public RoadSnapKind Kind { get; set; }

        /// <summary>吸附到節點時的節點代碼，否則為 null。</summary>
        public string? NodeCode { get; set; }

        /// <summary>吸附到路段時的那條路段（文件裡的物件），否則為 null。</summary>
        public LayoutRoadEdge? Edge { get; set; }
    }

    /// <summary>
    /// 路線編輯的點選與吸附：純計算，Unity 只負責把滑鼠位置換成地面座標再呼叫這裡，所以能用單元測試驗證。
    /// 距離比較用「小於等於半徑」，並容許浮點誤差。
    /// </summary>
    public static class RoadPicking
    {
        private const double Eps = 1e-6;

        /// <summary>半徑內最近的節點代碼；沒有就回傳 null。</summary>
        public static string? NearestNode(FactoryLayoutDocument document, double x, double z, double radius)
        {
            LayoutRoadNode? best = null;
            var bestDistance = double.MaxValue;
            foreach (var node in document.RoadNodes)
            {
                var distance = LayoutGeometry.Distance((node.X, node.Z), (x, z));
                if (distance <= radius + Eps && distance < bestDistance)
                {
                    best = node;
                    bestDistance = distance;
                }
            }

            return best?.Code;
        }

        /// <summary>半徑內離點最近的路段（點到線段的距離）；沒有就回傳 null。回傳的是文件裡的路段物件。</summary>
        public static LayoutRoadEdge? NearestEdge(FactoryLayoutDocument document, double x, double z, double radius) =>
            FindEdge(document, x, z, radius, out _, out _);

        /// <summary>
        /// 畫路時的吸附，優先順序：0.6 m 內的節點（取節點座標）→ 0.4 m 內的路段（投影到路段上）→ 0.2 m 格點。
        /// 半徑看 <see cref="LayoutRules"/>。結果取到 0.01 m。
        /// </summary>
        public static RoadSnap Snap(FactoryLayoutDocument document, double x, double z)
        {
            var nodeCode = NearestNode(document, x, z, LayoutRules.SnapNodeRadius);
            if (nodeCode != null)
            {
                var node = RoadGraphEditor.FindNode(document, nodeCode)!;
                return new RoadSnap { X = RoadGraphEditor.Round(node.X), Z = RoadGraphEditor.Round(node.Z), Kind = RoadSnapKind.Node, NodeCode = nodeCode };
            }

            var edge = FindEdge(document, x, z, LayoutRules.SnapEdgeRadius, out var p, out var q);
            if (edge != null)
            {
                var projected = Project((x, z), p, q);
                return new RoadSnap { X = RoadGraphEditor.Round(projected.X), Z = RoadGraphEditor.Round(projected.Z), Kind = RoadSnapKind.Edge, Edge = edge };
            }

            return new RoadSnap { X = GridValue(x), Z = GridValue(z), Kind = RoadSnapKind.Grid };
        }

        /// <summary>
        /// Shift 鎖軸：保留 |dx|、|dz| 較大的那一軸，另一軸等於起點，得到水平或垂直線。兩者相等時保留水平（X 軸）。
        /// </summary>
        public static (double X, double Z) LockAxis(double startX, double startZ, double endX, double endZ) =>
            Math.Abs(endX - startX) >= Math.Abs(endZ - startZ) ? (endX, startZ) : (startX, endZ);

        /// <summary>
        /// 鎖軸加吸附（Claude 暫定）：先鎖軸，再只沿鎖定的線吸附。
        /// 對鎖軸後的點做一般吸附；吸到節點或路段、而且吸附點仍在鎖定線上（0.01 m 內）才採用，
        /// 否則只對齊格點的自由軸，鎖定軸維持起點座標（讓線和起點完全水平或垂直）。
        /// </summary>
        public static RoadSnap SnapLocked(FactoryLayoutDocument document, double startX, double startZ, double x, double z)
        {
            var horizontal = Math.Abs(x - startX) >= Math.Abs(z - startZ);
            var locked = LockAxis(startX, startZ, x, z);
            var snap = Snap(document, locked.X, locked.Z);
            var onLine = horizontal ? Math.Abs(snap.Z - startZ) <= 0.01 + Eps : Math.Abs(snap.X - startX) <= 0.01 + Eps;
            if (snap.Kind != RoadSnapKind.Grid && onLine)
            {
                return snap;
            }

            return horizontal
                ? new RoadSnap { X = GridValue(locked.X), Z = RoadGraphEditor.Round(startZ), Kind = RoadSnapKind.Grid }
                : new RoadSnap { X = RoadGraphEditor.Round(startX), Z = GridValue(locked.Z), Kind = RoadSnapKind.Grid };
        }

        private static LayoutRoadEdge? FindEdge(FactoryLayoutDocument document, double x, double z, double radius, out (double X, double Z) a, out (double X, double Z) b)
        {
            LayoutRoadEdge? best = null;
            var bestDistance = double.MaxValue;
            a = default;
            b = default;
            foreach (var edge in document.RoadEdges)
            {
                var from = RoadGraphEditor.FindNode(document, edge.From);
                var to = RoadGraphEditor.FindNode(document, edge.To);
                if (from == null || to == null)
                {
                    continue;
                }

                var distance = LayoutGeometry.PointSegmentDistance((x, z), (from.X, from.Z), (to.X, to.Z));
                if (distance <= radius + Eps && distance < bestDistance)
                {
                    best = edge;
                    bestDistance = distance;
                    a = (from.X, from.Z);
                    b = (to.X, to.Z);
                }
            }

            return best;
        }

        private static double GridValue(double value) => LayoutEditSession.Snap(value, LayoutRules.RoadGrid);

        private static (double X, double Z) Project((double X, double Z) p, (double X, double Z) a, (double X, double Z) b)
        {
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var lengthSquared = dx * dx + dz * dz;
            if (lengthSquared < 1e-12)
            {
                return a;
            }

            var t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Z - a.Z) * dz) / lengthSquared));
            return (a.X + t * dx, a.Z + t * dz);
        }
    }
}
