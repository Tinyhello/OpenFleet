using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>
    /// 路網編輯：直接修改 <see cref="FactoryLayoutDocument"/> 的節點與路段（規劃第 7 節）。
    /// 所有座標一律四捨五入到 0.01 m；節點代碼是 <see cref="NodeCode"/>。常數在 <see cref="LayoutRules"/>。
    /// 模組自動接路（<see cref="ConnectModule"/> 等）用在移動、旋轉、新增、刪除模組；畫路線（<see cref="AddRoad"/>）留給第 5 步。
    /// 這些方法都在傳入的文件上直接修改，不複製。
    /// </summary>
    public static class RoadGraphEditor
    {
        // 浮點誤差的容許值：距離比較 0.3、0.01 時不要被 0.30000000000000004 擋掉。
        private const double Eps = 1e-6;

        /// <summary>座標四捨五入到 0.01 m；-0 變成 0。</summary>
        public static double Round(double value)
        {
            var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
            return rounded == 0 ? 0 : rounded;
        }

        /// <summary>節點代碼：x,z（最多兩位小數、invariant、-0 寫成 0）。預設佈局的節點代碼也是這個格式。</summary>
        public static string NodeCode(double x, double z) =>
            Round(x).ToString("0.##", CultureInfo.InvariantCulture) + "," + Round(z).ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>依代碼找節點；沒有就回傳 null。</summary>
        public static LayoutRoadNode? FindNode(FactoryLayoutDocument document, string code) =>
            document.RoadNodes.FirstOrDefault(n => n.Code == code);

        /// <summary>兩個節點之間是否有路段（不分方向）。</summary>
        public static bool HasEdge(FactoryLayoutDocument document, string a, string b) =>
            document.RoadEdges.Any(e => Matches(e, a, b));

        /// <summary>和節點相連的路段數。</summary>
        public static int EdgeCount(FactoryLayoutDocument document, string code) =>
            document.RoadEdges.Count(e => e.From == code || e.To == code);

        /// <summary>
        /// 確保 (x, z) 有一個節點並回傳代碼：0.3 m 內已有節點就回傳最近的那個；
        /// 點落在某條路段內部（0.01 m 內）就在那裡切開路段；否則新增節點。
        /// </summary>
        public static string EnsureNode(FactoryLayoutDocument document, double x, double z)
        {
            x = Round(x);
            z = Round(z);

            LayoutRoadNode? nearest = null;
            var nearestDistance = double.MaxValue;
            foreach (var node in document.RoadNodes)
            {
                var distance = LayoutGeometry.Distance((node.X, node.Z), (x, z));
                if (distance <= LayoutRules.MergeDistance + Eps && distance < nearestDistance)
                {
                    nearest = node;
                    nearestDistance = distance;
                }
            }

            if (nearest != null)
            {
                return nearest.Code;
            }

            LayoutRoadEdge? onEdge = null;
            var edgeDistance = double.MaxValue;
            foreach (var edge in document.RoadEdges)
            {
                var a = FindNode(document, edge.From);
                var b = FindNode(document, edge.To);
                if (a == null || b == null)
                {
                    continue;
                }

                var distance = LayoutGeometry.PointSegmentDistance((x, z), (a.X, a.Z), (b.X, b.Z));
                if (distance <= LayoutRules.OnEdgeDistance + Eps && distance < edgeDistance)
                {
                    onEdge = edge;
                    edgeDistance = distance;
                }
            }

            if (onEdge != null)
            {
                return SplitEdge(document, onEdge.From, onEdge.To, x, z) ?? NewNode(document, x, z);
            }

            return NewNode(document, x, z);
        }

        /// <summary>把 from–to 換成 from–新節點、新節點–to，回傳新節點的代碼；找不到那條路段時回傳 null。</summary>
        public static string? SplitEdge(FactoryLayoutDocument document, string from, string to, double x, double z)
        {
            var index = document.RoadEdges.FindIndex(e => Matches(e, from, to));
            if (index < 0)
            {
                return null;
            }

            var edge = document.RoadEdges[index];
            var first = edge.From;
            var second = edge.To;
            var code = NodeCode(x, z);
            if (code == first || code == second)
            {
                return code;
            }

            if (FindNode(document, code) == null)
            {
                document.RoadNodes.Add(new LayoutRoadNode { Code = code, X = Round(x), Z = Round(z) });
            }

            document.RoadEdges.RemoveAt(index);
            document.RoadEdges.Insert(index, new LayoutRoadEdge { From = first, To = code });
            document.RoadEdges.Insert(index + 1, new LayoutRoadEdge { From = code, To = second });
            return code;
        }

        /// <summary>
        /// 畫一條路（第 5 步用）：起點終點各 <see cref="EnsureNode"/>；和既有路段交叉的地方變成路口（既有路段被切開）；
        /// 剛好在路上的既有節點也串進來；相鄰兩點之間還沒有路段的，切成 ceil(長度 / 4 m) 段等長的路段。
        /// 回傳沿線的節點代碼（起點到終點）。起點與終點是同一個節點時只回傳那一個。
        /// </summary>
        public static List<string> AddRoad(FactoryLayoutDocument document, (double X, double Z) start, (double X, double Z) end)
        {
            var startCode = EnsureNode(document, start.X, start.Z);
            var endCode = EnsureNode(document, end.X, end.Z);
            if (startCode == endCode)
            {
                return new List<string> { startCode };
            }

            var startNode = FindNode(document, startCode)!;
            var endNode = FindNode(document, endCode)!;
            var p0 = (X: startNode.X, Z: startNode.Z);
            var p1 = (X: endNode.X, Z: endNode.Z);
            var lengthSquared = (p1.X - p0.X) * (p1.X - p0.X) + (p1.Z - p0.Z) * (p1.Z - p0.Z);

            // 沿線位置（0～1）：投影到起點終點的連線上。
            double Along(double x, double z) => ((x - p0.X) * (p1.X - p0.X) + (z - p0.Z) * (p1.Z - p0.Z)) / lengthSquared;

            var found = new Dictionary<string, double> { [startCode] = double.NegativeInfinity, [endCode] = double.PositiveInfinity };

            // 交叉：先記下現有的路段，逐條求交點；交點 EnsureNode 時會把那條路段切開。
            foreach (var edge in document.RoadEdges.ToList())
            {
                if (!document.RoadEdges.Contains(edge))
                {
                    continue;
                }

                var a = FindNode(document, edge.From);
                var b = FindNode(document, edge.To);
                if (a == null || b == null || !LayoutGeometry.SegmentsCross(p0, p1, (a.X, a.Z), (b.X, b.Z)))
                {
                    continue;
                }

                var point = Intersection(p0, p1, (a.X, a.Z), (b.X, b.Z));
                if (point == null)
                {
                    continue; // 平行：重疊的部分由下面「在路上的既有節點」處理
                }

                var code = EnsureNode(document, point.Value.X, point.Value.Z);
                if (!found.ContainsKey(code))
                {
                    var node = FindNode(document, code)!;
                    found[code] = Along(node.X, node.Z);
                }
            }

            // 剛好在新路段上的既有節點。
            foreach (var node in document.RoadNodes)
            {
                if (!found.ContainsKey(node.Code)
                    && LayoutGeometry.PointSegmentDistance((node.X, node.Z), p0, p1) <= LayoutRules.OnEdgeDistance + Eps)
                {
                    found[node.Code] = Along(node.X, node.Z);
                }
            }

            var ordered = found.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToList();
            var result = new List<string> { ordered[0] };
            for (var i = 0; i + 1 < ordered.Count; i++)
            {
                var from = ordered[i];
                var to = ordered[i + 1];
                if (!HasEdge(document, from, to))
                {
                    var a = FindNode(document, from)!;
                    var b = FindNode(document, to)!;
                    var length = LayoutGeometry.Distance((a.X, a.Z), (b.X, b.Z));
                    var parts = Math.Max(1, (int)Math.Ceiling(length / LayoutRules.MaxSegmentLength - 1e-9));
                    var previous = from;
                    for (var k = 1; k <= parts; k++)
                    {
                        var code = k == parts
                            ? to
                            : EnsureNode(document, a.X + (b.X - a.X) * k / parts, a.Z + (b.Z - a.Z) * k / parts);
                        if (code == previous)
                        {
                            continue;
                        }

                        AddEdge(document, previous, code);
                        if (code != to)
                        {
                            result.Add(code);
                        }

                        previous = code;
                    }
                }

                result.Add(to);
            }

            return result;
        }

        /// <summary>
        /// 移動節點。0.3 m 內有別的節點就合併過去（路段改接，去掉自己連自己與重複的路段），否則改座標與代碼，路段跟著改。
        /// 回傳移動後的節點代碼；找不到節點時回傳 null。
        /// </summary>
        public static string? MoveNode(FactoryLayoutDocument document, string code, double x, double z)
        {
            var node = FindNode(document, code);
            if (node == null)
            {
                return null;
            }

            x = Round(x);
            z = Round(z);

            LayoutRoadNode? target = null;
            var targetDistance = double.MaxValue;
            foreach (var other in document.RoadNodes)
            {
                if (other == node)
                {
                    continue;
                }

                var distance = LayoutGeometry.Distance((other.X, other.Z), (x, z));
                if (distance <= LayoutRules.MergeDistance + Eps && distance < targetDistance)
                {
                    target = other;
                    targetDistance = distance;
                }
            }

            if (target != null)
            {
                var edges = document.RoadEdges.ToList();
                document.RoadEdges.Clear();
                foreach (var edge in edges)
                {
                    var from = edge.From == code ? target.Code : edge.From;
                    var to = edge.To == code ? target.Code : edge.To;
                    if (from != to)
                    {
                        AddEdge(document, from, to);
                    }
                }

                document.RoadNodes.Remove(node);
                return target.Code;
            }

            var newCode = NodeCode(x, z);
            node.X = x;
            node.Z = z;
            if (newCode != code)
            {
                node.Code = newCode;
                foreach (var edge in document.RoadEdges)
                {
                    if (edge.From == code)
                    {
                        edge.From = newCode;
                    }

                    if (edge.To == code)
                    {
                        edge.To = newCode;
                    }
                }
            }

            return newCode;
        }

        /// <summary>刪除節點與相連的路段（相鄰的節點不動）。找不到節點時回傳 false。</summary>
        public static bool DeleteNode(FactoryLayoutDocument document, string code)
        {
            var node = FindNode(document, code);
            if (node == null)
            {
                return false;
            }

            document.RoadEdges.RemoveAll(e => e.From == code || e.To == code);
            document.RoadNodes.Remove(node);
            return true;
        }

        /// <summary>刪除路段；兩端如果因此沒有任何路段、又不是模組的交接點，一起刪掉。找不到路段時回傳 false。</summary>
        public static bool DeleteEdge(FactoryLayoutDocument document, string from, string to)
        {
            var removed = document.RoadEdges.RemoveAll(e => Matches(e, from, to));
            if (removed == 0)
            {
                return false;
            }

            RemoveIfIsolated(document, from);
            RemoveIfIsolated(document, to);
            return true;
        }

        /// <summary>
        /// 模組自動接路：從交接點沿 <see cref="LayoutGeometry.FacingDirection"/> 往外找 8 m 內第一條碰到的路段，
        /// 在碰到的點 <see cref="EnsureNode"/>，連一條交接點到那裡的單一路段（支線不切段：交通控制靠「只有一個出入口」判斷支線）。
        /// 使用者決定（2026-10-25，互動驗收發現）：車道節點每 4 m 一個，碰到的點常落在離節點不到 <see cref="LayoutRules.MinEdgeLength"/> 的地方，
        /// 在那裡切段會留下比最短路段還短的路段（實測 0.4 m）並觸發 edge-short／edge-clearance；
        /// 所以碰到的點離該路段較近的端點不到 MinEdgeLength 時，直接連到那個既有端點（不切段，支線略斜）；兩端都不到就取較近的那個。
        /// 沒有交接點、交接點已經接好、或 8 m 內沒有路時不接（驗證會報 handoff-missing／unreachable，讓使用者自己畫）。
        /// 回傳有沒有新增支線。
        /// </summary>
        public static bool ConnectModule(FactoryLayoutDocument document, LayoutModule module)
        {
            var handoff = LayoutGeometry.HandoffPoint(module);
            if (handoff == null)
            {
                return false;
            }

            var handoffCode = EnsureNode(document, handoff.Value.X, handoff.Value.Z);
            if (EdgeCount(document, handoffCode) > 0)
            {
                return false;
            }

            var origin = handoff.Value;
            var direction = LayoutGeometry.FacingDirection(module);
            var bestT = double.MaxValue;
            LayoutRoadNode? bestA = null;
            LayoutRoadNode? bestB = null;
            foreach (var edge in document.RoadEdges)
            {
                if (edge.From == handoffCode || edge.To == handoffCode)
                {
                    continue;
                }

                var a = FindNode(document, edge.From);
                var b = FindNode(document, edge.To);
                if (a != null && b != null
                    && RayHit(origin, direction, (a.X, a.Z), (b.X, b.Z), out var t)
                    && t < bestT)
                {
                    bestT = t;
                    bestA = a;
                    bestB = b;
                }
            }

            if (bestT > LayoutRules.MaxSpurLength + Eps)
            {
                return false;
            }

            var hitX = origin.X + direction.X * bestT;
            var hitZ = origin.Z + direction.Z * bestT;
            string hit;
            var distA = LayoutGeometry.Distance((bestA!.X, bestA.Z), (hitX, hitZ));
            var distB = LayoutGeometry.Distance((bestB!.X, bestB.Z), (hitX, hitZ));
            var nearerIsA = distA <= distB;
            var nearerDistance = nearerIsA ? distA : distB;
            if (nearerDistance < LayoutRules.MinEdgeLength - Eps)
            {
                // 離端點太近：連到既有端點，不新增節點。
                hit = nearerIsA ? bestA.Code : bestB.Code;
            }
            else
            {
                hit = EnsureNode(document, hitX, hitZ);
            }

            if (hit == handoffCode)
            {
                return false;
            }

            AddEdge(document, handoffCode, hit);
            return true;
        }

        /// <summary>
        /// 拆掉模組的接路：刪掉交接點上的節點與相連的路段；原本接上的路口如果只剩兩條路段、兩條在一直線上、
        /// 合起來不超過 4.01 m、又不是別的模組的交接點，就把它拿掉、兩段合成一段（避免反覆移動模組後路上留下一堆節點）；
        /// 變成沒有路段的節點刪掉（交接點除外）。
        /// </summary>
        public static void DisconnectModule(FactoryLayoutDocument document, LayoutModule module)
        {
            var handoff = LayoutGeometry.HandoffPoint(module);
            if (handoff == null)
            {
                return;
            }

            LayoutRoadNode? handoffNode = null;
            var best = double.MaxValue;
            foreach (var node in document.RoadNodes)
            {
                var distance = LayoutGeometry.Distance((node.X, node.Z), handoff.Value);
                if (distance <= LayoutRules.HandoffMatch + Eps && distance < best)
                {
                    handoffNode = node;
                    best = distance;
                }
            }

            if (handoffNode == null)
            {
                return;
            }

            var neighbours = document.RoadEdges
                .Where(e => e.From == handoffNode.Code || e.To == handoffNode.Code)
                .Select(e => e.From == handoffNode.Code ? e.To : e.From)
                .Distinct()
                .ToList();
            DeleteNode(document, handoffNode.Code);

            foreach (var code in neighbours)
            {
                var neighbour = FindNode(document, code);
                if (neighbour == null || IsHandoffOfAnyModule(document, neighbour))
                {
                    continue;
                }

                var edges = document.RoadEdges.Where(e => e.From == code || e.To == code).ToList();
                if (edges.Count == 0)
                {
                    document.RoadNodes.Remove(neighbour);
                    continue;
                }

                if (edges.Count != 2)
                {
                    continue;
                }

                var first = FindNode(document, edges[0].From == code ? edges[0].To : edges[0].From);
                var second = FindNode(document, edges[1].From == code ? edges[1].To : edges[1].From);
                if (first == null || second == null || first == second)
                {
                    continue;
                }

                var length = LayoutGeometry.Distance((neighbour.X, neighbour.Z), (first.X, first.Z))
                    + LayoutGeometry.Distance((neighbour.X, neighbour.Z), (second.X, second.Z));
                var collinear = LayoutGeometry.PointSegmentDistance((neighbour.X, neighbour.Z), (first.X, first.Z), (second.X, second.Z))
                    <= LayoutRules.OnEdgeDistance + Eps;
                if (!collinear || length > LayoutRules.MaxSegmentLength + 0.01 + Eps)
                {
                    continue;
                }

                var index = document.RoadEdges.IndexOf(edges[0]);
                document.RoadEdges.RemoveAll(e => e.From == code || e.To == code);
                document.RoadNodes.Remove(neighbour);
                if (!HasEdge(document, first.Code, second.Code))
                {
                    document.RoadEdges.Insert(Math.Min(index, document.RoadEdges.Count), new LayoutRoadEdge { From = first.Code, To = second.Code });
                }
            }
        }

        /// <summary>移動或旋轉模組：拆掉舊位置（<paramref name="before"/>）的接路，再接新位置（<paramref name="after"/>）。</summary>
        public static void ReconnectModule(FactoryLayoutDocument document, LayoutModule before, LayoutModule after)
        {
            DisconnectModule(document, before);
            ConnectModule(document, after);
        }

        /// <summary>刪除模組：拆掉接路並從清單移除。找不到模組時回傳 false。</summary>
        public static bool RemoveModule(FactoryLayoutDocument document, string code)
        {
            var module = document.Modules.FirstOrDefault(m => m.Code == code);
            if (module == null)
            {
                return false;
            }

            DisconnectModule(document, module);
            document.Modules.Remove(module);
            return true;
        }

        private static bool Matches(LayoutRoadEdge edge, string a, string b) =>
            (edge.From == a && edge.To == b) || (edge.From == b && edge.To == a);

        private static string NewNode(FactoryLayoutDocument document, double x, double z)
        {
            var code = NodeCode(x, z);
            if (FindNode(document, code) == null)
            {
                document.RoadNodes.Add(new LayoutRoadNode { Code = code, X = Round(x), Z = Round(z) });
            }

            return code;
        }

        // 加路段；兩端相同或已經有同一條路段就不加。
        private static void AddEdge(FactoryLayoutDocument document, string from, string to)
        {
            if (from != to && !HasEdge(document, from, to))
            {
                document.RoadEdges.Add(new LayoutRoadEdge { From = from, To = to });
            }
        }

        private static bool IsHandoffOfAnyModule(FactoryLayoutDocument document, LayoutRoadNode node)
        {
            foreach (var module in document.Modules)
            {
                var handoff = LayoutGeometry.HandoffPoint(module);
                if (handoff != null && LayoutGeometry.Distance((node.X, node.Z), handoff.Value) <= LayoutRules.HandoffMatch + Eps)
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveIfIsolated(FactoryLayoutDocument document, string code)
        {
            var node = FindNode(document, code);
            if (node != null && EdgeCount(document, code) == 0 && !IsHandoffOfAnyModule(document, node))
            {
                document.RoadNodes.Remove(node);
            }
        }

        // 兩條不平行線段所在直線的交點；平行（含共線）時回傳 null。
        private static (double X, double Z)? Intersection((double X, double Z) a1, (double X, double Z) a2, (double X, double Z) b1, (double X, double Z) b2)
        {
            var dx = a2.X - a1.X;
            var dz = a2.Z - a1.Z;
            var ex = b2.X - b1.X;
            var ez = b2.Z - b1.Z;
            var denominator = dx * ez - dz * ex;
            if (Math.Abs(denominator) < 1e-12)
            {
                return null;
            }

            var t = ((b1.X - a1.X) * ez - (b1.Z - a1.Z) * ex) / denominator;
            return (a1.X + t * dx, a1.Z + t * dz);
        }

        // 射線（起點、單位方向）碰到線段的距離 t（t 大於 0）。平行時只有共線才算，取較近的端點。
        private static bool RayHit((double X, double Z) origin, (double X, double Z) direction, (double X, double Z) a, (double X, double Z) b, out double t)
        {
            t = 0;
            var ex = b.X - a.X;
            var ez = b.Z - a.Z;
            var wx = a.X - origin.X;
            var wz = a.Z - origin.Z;
            var denominator = direction.X * ez - direction.Z * ex;
            if (Math.Abs(denominator) < 1e-9)
            {
                if (Math.Abs(wx * direction.Z - wz * direction.X) > Eps)
                {
                    return false;
                }

                var ta = wx * direction.X + wz * direction.Z;
                var tb = (b.X - origin.X) * direction.X + (b.Z - origin.Z) * direction.Z;
                var near = Math.Min(ta, tb);
                var far = Math.Max(ta, tb);
                if (far <= Eps)
                {
                    return false;
                }

                t = near > Eps ? near : far;
                return near > Eps;
            }

            t = (wx * ez - wz * ex) / denominator;
            var s = (wx * direction.Z - wz * direction.X) / denominator;
            return t > Eps && s >= -Eps && s <= 1 + Eps;
        }
    }
}
