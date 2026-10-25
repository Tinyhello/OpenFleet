using System;
using System.Collections.Generic;
using System.Linq;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>
    /// 廠房形狀的統一入口：舊文件只有 Min／Max 的矩形（沒有 Outline 與 Gate），新文件有外牆頂點與大門。
    /// 驗證、編輯、後端與 Unity 一律透過這裡取形狀，不直接讀 Min／Max，舊文件才不用轉換就能繼續用。
    /// 回傳的都是新的物件，改它們不會動到文件。
    /// </summary>
    public static class LayoutFloorShape
    {
        /// <summary>
        /// 外牆頂點（逆時針或文件原本的順序）。Outline 是 null 或空時，回傳 Min／Max 的矩形，
        /// 順序逆時針：(MinX,MinZ)、(MaxX,MinZ)、(MaxX,MaxZ)、(MinX,MaxZ)。Outline 裡的 null 項目會略過。
        /// </summary>
        public static List<LayoutPoint> Outline(LayoutFloor floor)
        {
            if (floor.Outline != null && floor.Outline.Count > 0)
            {
                return floor.Outline.Where(p => p != null).Select(p => new LayoutPoint { X = p.X, Z = p.Z }).ToList();
            }

            return new List<LayoutPoint>
            {
                new LayoutPoint { X = floor.MinX, Z = floor.MinZ },
                new LayoutPoint { X = floor.MaxX, Z = floor.MinZ },
                new LayoutPoint { X = floor.MaxX, Z = floor.MaxZ },
                new LayoutPoint { X = floor.MinX, Z = floor.MaxZ },
            };
        }

        /// <summary>
        /// 大門中心。Gate 是 null 時（舊文件）回傳最西邊那段牆的中點：以牆中點的 X 最小者為準，
        /// 並列時取中點 Z 較小的。外牆不到 3 個頂點時回傳外框的西邊中點。
        /// </summary>
        public static LayoutPoint Gate(LayoutFloor floor)
        {
            if (floor.Gate != null)
            {
                return new LayoutPoint { X = floor.Gate.X, Z = floor.Gate.Z };
            }

            var outline = Outline(floor);
            if (outline.Count < 2)
            {
                return new LayoutPoint { X = floor.MinX, Z = (floor.MinZ + floor.MaxZ) / 2 };
            }

            var best = -1;
            (double X, double Z) bestMid = (0, 0);
            for (var i = 0; i < outline.Count; i++)
            {
                var mid = FloorGeometry.WallMidpoint(outline, i);
                if (best < 0 || mid.X < bestMid.X - 1e-6 || (Math.Abs(mid.X - bestMid.X) <= 1e-6 && mid.Z < bestMid.Z))
                {
                    best = i;
                    bestMid = mid;
                }
            }

            return new LayoutPoint { X = bestMid.X, Z = bestMid.Z };
        }

        /// <summary>頂點的外框（軸對齊矩形）。沒有頂點時是全 0 的矩形。</summary>
        public static LayoutRect Bounds(IReadOnlyList<LayoutPoint> outline)
        {
            if (outline.Count == 0)
            {
                return new LayoutRect(0, 0, 0, 0);
            }

            return new LayoutRect(outline.Min(p => p.X), outline.Max(p => p.X), outline.Min(p => p.Z), outline.Max(p => p.Z));
        }

        /// <summary>廠房的外框。</summary>
        public static LayoutRect Bounds(LayoutFloor floor) => Bounds(Outline(floor));

        /// <summary>外牆段數。</summary>
        public static int WallCount(LayoutFloor floor) => Outline(floor).Count;

        /// <summary>廠房面積（平方公尺）。</summary>
        public static double Area(LayoutFloor floor) => FloorGeometry.Area(Outline(floor));

        /// <summary>
        /// 大門所在的牆編號：大門中心離某段牆 <see cref="LayoutRules.GateOnWallTolerance"/> 以內（取最近的）；
        /// 不在任何牆上回傳 -1。
        /// </summary>
        public static int GateWallIndex(LayoutFloor floor)
        {
            var outline = Outline(floor);
            var gate = Gate(floor);
            var hit = outline.Count == 0 ? null : FloorGeometry.NearestWall(outline, gate.X, gate.Z);
            return hit != null && hit.Distance <= LayoutRules.GateOnWallTolerance ? hit.Index : -1;
        }
    }
}
