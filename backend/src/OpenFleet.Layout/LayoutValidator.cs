using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>
    /// 佈局驗證（規則表見第 5 節第 5 項）。只回報問題，不改動文件。
    /// IsValid = 沒有錯誤；警告不影響套用。訊息用中文，帶上模組名稱或節點座標。
    /// </summary>
    public static class LayoutValidator
    {
        /// <summary>
        /// 結構錯誤的問題代碼：文件缺欄位、清單有空項目、種類不是定義過的值。
        /// 這種文件後面的檢查都沒辦法跑，存檔也要擋下來（見 LayoutService）。
        /// </summary>
        public const string DocumentInvalid = "document-invalid";

        // 結構錯誤的訊息最多列出幾個項目編號，其餘用「…」帶過（文件可以任意大，訊息不能跟著變長）。
        private const int MaxListedIndexes = 5;

        /// <summary>
        /// 驗證一份佈局。文件來自 JSON，欄位可能是 null、種類可能是任意整數，所以先檢查結構；
        /// 結構有錯就只回傳結構錯誤，不跑其他檢查。任何輸入都不會丟出例外。
        /// </summary>
        public static LayoutValidationDto Validate(FactoryLayoutDocument? document)
        {
            var issues = new List<LayoutIssueDto>();

            if (!IsWellFormed(document, issues))
            {
                return Result(issues);
            }

            CheckFloor(document, issues);
            CheckLimits(document, issues);
            CheckCodes(document, issues);
            CheckRotations(document, issues);
            CheckFootprints(document, issues);
            CheckFacilities(document, issues);
            CheckProcesses(document, issues);
            CheckVehicles(document, issues);

            var nodes = FirstByCode(document.RoadNodes);
            var edges = CheckEdges(document, nodes, issues);
            CheckThroughModules(document, edges, issues);
            var handoffNodes = CheckHandoffs(document, nodes, edges, issues);
            CheckReachability(document, handoffNodes, edges, issues);
            CheckIsolatedNodes(document, handoffNodes, issues);

            return Result(issues);
        }

        private static LayoutValidationDto Result(List<LayoutIssueDto> issues) => new LayoutValidationDto
        {
            IsValid = !issues.Any(i => i.Severity == LayoutIssueSeverity.Error),
            Issues = issues,
        };

        // 結構檢查：後面的每一項檢查都假設欄位存在、項目不是 null、代碼有字、種類在目錄裡。
        // JSON 可以把這些都弄壞（"modules": null、清單裡有 null、"kind": 99），所以全部在這裡擋下來，後面就不用再防。
        // 同一種問題只回報一則，列出前幾個項目的編號（從 1 起）。
        private static bool IsWellFormed([NotNullWhen(true)] FactoryLayoutDocument? document, List<LayoutIssueDto> issues)
        {
            if (document == null)
            {
                AddError(issues, DocumentInvalid, "佈局文件不能是空的");
                return false;
            }

            if (document.Floor == null)
            {
                AddError(issues, DocumentInvalid, "缺少廠房範圍（floor）");
            }

            if (document.Modules == null)
            {
                AddError(issues, DocumentInvalid, "缺少模組清單（modules）");
            }
            else
            {
                var empty = new List<int>();
                var noCode = new List<int>();
                var unknownKind = new List<int>();
                for (var i = 0; i < document.Modules.Count; i++)
                {
                    var module = document.Modules[i];
                    if (module == null)
                    {
                        empty.Add(i);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(module.Code))
                    {
                        noCode.Add(i);
                    }

                    if (!Enum.IsDefined(typeof(ModuleKind), module.Kind))
                    {
                        unknownKind.Add(i);
                    }
                }

                AddIndexed(issues, empty, "模組清單（modules）裡有空的項目");
                AddIndexed(issues, noCode, "模組的代碼（code）是空的");
                AddIndexed(issues, unknownKind, "模組的種類（kind）不是已定義的種類");
            }

            if (document.RoadNodes == null)
            {
                AddError(issues, DocumentInvalid, "缺少路網節點清單（roadNodes）");
            }
            else
            {
                var empty = new List<int>();
                var noCode = new List<int>();
                for (var i = 0; i < document.RoadNodes.Count; i++)
                {
                    var node = document.RoadNodes[i];
                    if (node == null)
                    {
                        empty.Add(i);
                    }
                    else if (string.IsNullOrWhiteSpace(node.Code))
                    {
                        noCode.Add(i);
                    }
                }

                AddIndexed(issues, empty, "路網節點清單（roadNodes）裡有空的項目");
                AddIndexed(issues, noCode, "路網節點的代碼（code）是空的");
            }

            if (document.RoadEdges == null)
            {
                AddError(issues, DocumentInvalid, "缺少路段清單（roadEdges）");
            }
            else
            {
                var empty = new List<int>();
                var noEnd = new List<int>();
                for (var i = 0; i < document.RoadEdges.Count; i++)
                {
                    var edge = document.RoadEdges[i];
                    if (edge == null)
                    {
                        empty.Add(i);
                    }
                    else if (edge.From == null || edge.To == null)
                    {
                        noEnd.Add(i);
                    }
                }

                AddIndexed(issues, empty, "路段清單（roadEdges）裡有空的項目");
                AddIndexed(issues, noEnd, "路段的起點或終點（from／to）是空的");
            }

            return issues.Count == 0;
        }

        // 沒有項目就不回報；有的話列出前 MaxListedIndexes 個編號。
        private static void AddIndexed(List<LayoutIssueDto> issues, List<int> indexes, string what)
        {
            if (indexes.Count == 0)
            {
                return;
            }

            var listed = string.Join("、", indexes.Take(MaxListedIndexes).Select(i => (i + 1).ToString(CultureInfo.InvariantCulture)));
            var more = indexes.Count > MaxListedIndexes ? $"…（共 {indexes.Count} 項）" : "";
            AddError(issues, DocumentInvalid, $"{what}：第 {listed} 項{more}");
        }

        // 一條有效的路段（端點存在、不是自己連自己、不重複）。
        private sealed class Edge
        {
            public LayoutRoadEdge Source { get; set; } = new LayoutRoadEdge();
            public string From { get; set; } = "";
            public string To { get; set; } = "";
            public (double X, double Z) A { get; set; }
            public (double X, double Z) B { get; set; }
        }

        private static void CheckFloor(FactoryLayoutDocument document, List<LayoutIssueDto> issues)
        {
            var floor = document.Floor;
            var width = floor.MaxX - floor.MinX;
            var depth = floor.MaxZ - floor.MinZ;
            var tolerance = LayoutRules.Tolerance;
            if (width < LayoutRules.MinFloorWidth - tolerance || width > LayoutRules.MaxFloorWidth + tolerance
                || depth < LayoutRules.MinFloorDepth - tolerance || depth > LayoutRules.MaxFloorDepth + tolerance)
            {
                AddError(issues, "floor-size", $"廠房寬 40～100 m、深 24～60 m（目前寬 {Fmt(width)} m、深 {Fmt(depth)} m）");
            }
        }

        private static void CheckLimits(FactoryLayoutDocument document, List<LayoutIssueDto> issues)
        {
            if (document.Modules.Count > LayoutRules.MaxModules)
            {
                AddError(issues, "too-many", $"模組最多 {LayoutRules.MaxModules} 個（目前 {document.Modules.Count} 個）");
            }

            if (document.RoadNodes.Count > LayoutRules.MaxRoadNodes)
            {
                AddError(issues, "too-many", $"路網節點最多 {LayoutRules.MaxRoadNodes} 個（目前 {document.RoadNodes.Count} 個）");
            }

            var bays = document.Modules.Count(m => ModuleCatalog.IsBay(m.Kind));
            if (bays > LayoutRules.MaxVehicles)
            {
                AddError(issues, "too-many", $"AGV 最多 {LayoutRules.MaxVehicles} 台（目前 {bays} 台）");
            }
        }

        private static void CheckCodes(FactoryLayoutDocument document, List<LayoutIssueDto> issues)
        {
            foreach (var group in document.Modules.GroupBy(m => m.Code).Where(g => g.Count() > 1))
            {
                AddError(issues, "code-invalid", $"模組代碼重複：{group.Key}", modules: new[] { group.Key });
            }

            foreach (var module in document.Modules)
            {
                if (!ModuleCatalog.IsValidCode(module.Kind, module.Code))
                {
                    AddError(
                        issues,
                        "code-invalid",
                        $"{ModuleNaming.DisplayName(document, module)} 的代碼「{module.Code}」不符合規則（應為 {Pattern(module.Kind)}）",
                        modules: new[] { module.Code });
                }
            }

            foreach (var group in document.RoadNodes.GroupBy(n => n.Code).Where(g => g.Count() > 1))
            {
                AddError(issues, "code-invalid", $"路網節點代碼重複：{group.Key}", nodes: new[] { group.Key });
            }
        }

        private static void CheckRotations(FactoryLayoutDocument document, List<LayoutIssueDto> issues)
        {
            foreach (var module in document.Modules)
            {
                if (!LayoutRules.AllowedRotations.Contains(module.Rotation))
                {
                    AddError(
                        issues,
                        "rotation-invalid",
                        $"{ModuleNaming.DisplayName(document, module)} 的朝向 {module.Rotation} 不合法（只能是 0、90、180、270）",
                        modules: new[] { module.Code });
                }
            }
        }

        private static void CheckFootprints(FactoryLayoutDocument document, List<LayoutIssueDto> issues)
        {
            var floor = document.Floor;
            var floorRect = new LayoutRect(floor.MinX, floor.MaxX, floor.MinZ, floor.MaxZ);
            var footprints = document.Modules.Select(m => (Module: m, Rect: LayoutGeometry.Footprint(m))).ToList();

            foreach (var (module, rect) in footprints)
            {
                if (!LayoutGeometry.RectInside(rect, floorRect))
                {
                    AddError(issues, "module-outside", $"{ModuleNaming.DisplayName(document, module)} 的占地超出廠房", modules: new[] { module.Code });
                }
            }

            for (var i = 0; i < footprints.Count; i++)
            {
                for (var j = i + 1; j < footprints.Count; j++)
                {
                    var a = footprints[i];
                    var b = footprints[j];
                    if (LayoutGeometry.RectsOverlap(a.Rect, b.Rect))
                    {
                        AddError(
                            issues,
                            "module-overlap",
                            $"{ModuleNaming.DisplayName(document, a.Module)} 與 {ModuleNaming.DisplayName(document, b.Module)} 的占地重疊",
                            modules: new[] { a.Module.Code, b.Module.Code });
                    }
                }
            }
        }

        private static void CheckFacilities(FactoryLayoutDocument document, List<LayoutIssueDto> issues)
        {
            foreach (var kind in new[] { ModuleKind.RawMaterialStore, ModuleKind.FinishedGoodsStore, ModuleKind.ShippingDock, ModuleKind.ToolStore })
            {
                var same = document.Modules.Where(m => m.Kind == kind).ToList();
                if (same.Count != 1)
                {
                    AddError(
                        issues,
                        "facility-count",
                        $"{ModuleCatalog.Get(kind).ZhName}要剛好一個（目前 {same.Count} 個）",
                        modules: same.Select(m => m.Code));
                }
            }
        }

        private static void CheckProcesses(FactoryLayoutDocument document, List<LayoutIssueDto> issues)
        {
            foreach (var code in ModuleCatalog.ProcessCodes)
            {
                if (ModuleCatalog.ProcessKindOf(code) is not ModuleKind kind)
                {
                    continue;
                }

                if (!document.Modules.Any(m => m.Kind == kind))
                {
                    AddError(issues, "step-missing", $"工序「{ModuleCatalog.Get(kind).ZhName}」沒有機台（九道工序每一道至少要有一台）");
                }
            }
        }

        private static void CheckVehicles(FactoryLayoutDocument document, List<LayoutIssueDto> issues)
        {
            if (!document.Modules.Any(m => m.Kind == ModuleKind.AgvBayLarge))
            {
                AddError(issues, "vehicle-missing", "至少要有一台大型 AGV（搬料）");
            }

            if (!document.Modules.Any(m => m.Kind == ModuleKind.AgvBaySmall))
            {
                AddError(issues, "vehicle-missing", "至少要有一台小型 AGV（送刀具）");
            }
        }

        // 路段的基本檢查（edge-invalid、edge-short）與兩兩之間的交叉與距離（edge-crossing、edge-clearance）。
        // 回傳通過基本檢查的路段，後面的檢查只用這些。
        private static List<Edge> CheckEdges(FactoryLayoutDocument document, Dictionary<string, LayoutRoadNode> nodes, List<LayoutIssueDto> issues)
        {
            var valid = new List<Edge>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in document.RoadEdges)
            {
                if (!nodes.TryGetValue(edge.From ?? "", out var a) || !nodes.TryGetValue(edge.To ?? "", out var b))
                {
                    AddError(issues, "edge-invalid", $"路段指到不存在的節點：{edge.From}–{edge.To}", edges: new[] { edge });
                    continue;
                }

                if (edge.From == edge.To)
                {
                    AddError(issues, "edge-invalid", $"路段連到自己：{edge.From}", edges: new[] { edge });
                    continue;
                }

                var key = string.CompareOrdinal(edge.From, edge.To) <= 0 ? edge.From + "|" + edge.To : edge.To + "|" + edge.From;
                if (!seen.Add(key))
                {
                    AddError(issues, "edge-invalid", $"重複的路段：{edge.From}–{edge.To}", edges: new[] { edge });
                    continue;
                }

                var item = new Edge
                {
                    Source = edge,
                    From = edge.From ?? "",
                    To = edge.To ?? "",
                    A = (a.X, a.Z),
                    B = (b.X, b.Z),
                };
                var length = LayoutGeometry.Distance(item.A, item.B);
                if (length < LayoutRules.MinEdgeLength - LayoutRules.Tolerance)
                {
                    AddError(issues, "edge-short", $"路段 {edge.From}–{edge.To} 只有 {Fmt(length)} m，短於 1.6 m", edges: new[] { edge });
                }

                valid.Add(item);
            }

            for (var i = 0; i < valid.Count; i++)
            {
                for (var j = i + 1; j < valid.Count; j++)
                {
                    var p = valid[i];
                    var q = valid[j];
                    var pair = new[] { p.Source, q.Source };
                    if (LayoutGeometry.SegmentsCross(p.A, p.B, q.A, q.B))
                    {
                        AddError(issues, "edge-crossing", $"路段 {p.From}–{p.To} 與 {q.From}–{q.To} 交叉或碰到（交叉處要有路口節點）", edges: pair);
                        continue;
                    }

                    if (SharesNode(p, q))
                    {
                        continue;
                    }

                    var distance = LayoutGeometry.SegmentDistance(p.A, p.B, q.A, q.B);
                    if (distance < LayoutRules.MinClearance - LayoutRules.Tolerance)
                    {
                        AddError(
                            issues,
                            "edge-clearance",
                            $"路段 {p.From}–{p.To} 與 {q.From}–{q.To} 相距 {Fmt(distance)} m，低於 1.6 m（沒有共用節點）",
                            edges: pair);
                    }
                }
            }

            return valid;
        }

        // 路段穿過模組的占地（占地往內縮 0.05 m 再判斷）；接到這個模組自己交接點的路段不算。
        private static void CheckThroughModules(FactoryLayoutDocument document, List<Edge> edges, List<LayoutIssueDto> issues)
        {
            foreach (var module in document.Modules)
            {
                var rect = LayoutGeometry.Footprint(module).Shrink(LayoutRules.ThroughInset);
                var handoff = LayoutGeometry.HandoffPoint(module);
                foreach (var edge in edges)
                {
                    if (handoff.HasValue && (Near(edge.A, handoff.Value) || Near(edge.B, handoff.Value)))
                    {
                        continue;
                    }

                    if (LayoutGeometry.SegmentIntersectsRect(edge.A, edge.B, rect))
                    {
                        AddError(
                            issues,
                            "edge-through-module",
                            $"路段 {edge.From}–{edge.To} 穿過 {ModuleNaming.DisplayName(document, module)} 的占地",
                            modules: new[] { module.Code },
                            edges: new[] { edge.Source });
                    }
                }
            }
        }

        // 有交接點的模組，交接點上要有路網節點，而且那個節點至少有一條路段。
        // 回傳每個模組的交接節點代碼（給連通性檢查用）。
        private static Dictionary<string, string> CheckHandoffs(
            FactoryLayoutDocument document,
            Dictionary<string, LayoutRoadNode> nodes,
            List<Edge> edges,
            List<LayoutIssueDto> issues)
        {
            var degree = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                degree[edge.From] = degree.TryGetValue(edge.From, out var fromCount) ? fromCount + 1 : 1;
                degree[edge.To] = degree.TryGetValue(edge.To, out var toCount) ? toCount + 1 : 1;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var module in document.Modules)
            {
                var handoff = LayoutGeometry.HandoffPoint(module);
                if (!handoff.HasValue)
                {
                    continue;
                }

                var name = ModuleNaming.DisplayName(document, module);
                LayoutRoadNode? found = null;
                var best = double.MaxValue;
                foreach (var node in nodes.Values)
                {
                    var distance = LayoutGeometry.Distance((node.X, node.Z), handoff.Value);
                    if (distance <= LayoutRules.HandoffMatch && distance < best)
                    {
                        best = distance;
                        found = node;
                    }
                }

                if (found == null)
                {
                    AddError(
                        issues,
                        "handoff-missing",
                        $"{name} 的交接點（{Fmt(handoff.Value.X)}, {Fmt(handoff.Value.Z)}）上沒有路網節點",
                        modules: new[] { module.Code });
                    continue;
                }

                result[module.Code] = found.Code;
                var count = degree.TryGetValue(found.Code, out var c) ? c : 0;
                if (count == 0)
                {
                    AddError(
                        issues,
                        "handoff-missing",
                        $"{name} 的交接點節點 {found.Code} 沒有任何路段",
                        modules: new[] { module.Code },
                        nodes: new[] { found.Code });
                }

                if (ModuleCatalog.IsStation(module.Kind) && count >= 2)
                {
                    AddWarning(
                        issues,
                        "handoff-through",
                        $"{name} 的交接點有 {count} 條路段，停在上面交接的車會擋住路過的車",
                        modules: new[] { module.Code },
                        nodes: new[] { found.Code });
                }
            }

            return result;
        }

        // 所有交接點（工站與車位）都要在原料倉交接點所在的連通區塊裡。
        private static void CheckReachability(
            FactoryLayoutDocument document,
            Dictionary<string, string> handoffNodes,
            List<Edge> edges,
            List<LayoutIssueDto> issues)
        {
            var raw = document.Modules.FirstOrDefault(m => m.Kind == ModuleKind.RawMaterialStore);
            if (raw == null || !handoffNodes.TryGetValue(raw.Code, out var start))
            {
                return;
            }

            var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                AddNeighbour(adjacency, edge.From, edge.To);
                AddNeighbour(adjacency, edge.To, edge.From);
            }

            var reached = new HashSet<string>(StringComparer.Ordinal) { start };
            var queue = new Queue<string>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out var next))
                {
                    continue;
                }

                foreach (var code in next)
                {
                    if (reached.Add(code))
                    {
                        queue.Enqueue(code);
                    }
                }
            }

            var unreachable = document.Modules
                .Where(m => handoffNodes.TryGetValue(m.Code, out var node) && !reached.Contains(node))
                .ToList();
            if (unreachable.Count > 0)
            {
                AddError(
                    issues,
                    "unreachable",
                    "不在原料倉交接點的連通路網裡：" + string.Join("、", unreachable.Select(m => ModuleNaming.DisplayName(document, m))),
                    modules: unreachable.Select(m => m.Code),
                    // 讓編輯器能把斷掉的路畫成紅色：斷掉的交接點節點，加上不在原料倉連通區塊裡的路段。
                    nodes: unreachable.Select(m => handoffNodes[m.Code]).Distinct(StringComparer.Ordinal),
                    edges: edges
                        .Where(e => !reached.Contains(e.From))
                        .Select(e => new LayoutRoadEdge { From = e.From, To = e.To }));
            }
        }

        // 沒有任何路段、也不是交接點的節點。
        private static void CheckIsolatedNodes(FactoryLayoutDocument document, Dictionary<string, string> handoffNodes, List<LayoutIssueDto> issues)
        {
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in document.RoadEdges)
            {
                touched.Add(edge.From ?? "");
                touched.Add(edge.To ?? "");
            }

            var handoffCodes = new HashSet<string>(handoffNodes.Values, StringComparer.Ordinal);
            foreach (var node in FirstByCode(document.RoadNodes).Values)
            {
                if (!touched.Contains(node.Code) && !handoffCodes.Contains(node.Code))
                {
                    AddWarning(issues, "node-isolated", $"節點 {node.Code} 沒有任何路段，也不是交接點", nodes: new[] { node.Code });
                }
            }
        }

        private static bool SharesNode(Edge p, Edge q) =>
            p.From == q.From || p.From == q.To || p.To == q.From || p.To == q.To;

        private static bool Near((double X, double Z) point, (double X, double Z) handoff) =>
            LayoutGeometry.Distance(point, handoff) <= LayoutRules.HandoffMatch;

        private static Dictionary<string, LayoutRoadNode> FirstByCode(List<LayoutRoadNode> nodes)
        {
            var result = new Dictionary<string, LayoutRoadNode>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                if (!result.ContainsKey(node.Code))
                {
                    result[node.Code] = node;
                }
            }

            return result;
        }

        private static void AddNeighbour(Dictionary<string, List<string>> adjacency, string from, string to)
        {
            if (!adjacency.TryGetValue(from, out var list))
            {
                list = new List<string>();
                adjacency[from] = list;
            }

            list.Add(to);
        }

        private static string Pattern(ModuleKind kind)
        {
            var spec = ModuleCatalog.Get(kind);
            if (spec.ProcessCode != null)
            {
                return spec.ProcessCode + "-編號";
            }

            if (spec.FixedCode != null)
            {
                return spec.FixedCode;
            }

            if (ModuleCatalog.IsBay(kind))
            {
                return "agv-NN";
            }

            return kind == ModuleKind.MiscStore ? "misc-N" : "carrier-N";
        }

        private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static void AddError(
            List<LayoutIssueDto> issues,
            string code,
            string message,
            IEnumerable<string>? modules = null,
            IEnumerable<string>? nodes = null,
            IEnumerable<LayoutRoadEdge>? edges = null) =>
            Add(issues, LayoutIssueSeverity.Error, code, message, modules, nodes, edges);

        private static void AddWarning(
            List<LayoutIssueDto> issues,
            string code,
            string message,
            IEnumerable<string>? modules = null,
            IEnumerable<string>? nodes = null,
            IEnumerable<LayoutRoadEdge>? edges = null) =>
            Add(issues, LayoutIssueSeverity.Warning, code, message, modules, nodes, edges);

        private static void Add(
            List<LayoutIssueDto> issues,
            LayoutIssueSeverity severity,
            string code,
            string message,
            IEnumerable<string>? modules,
            IEnumerable<string>? nodes,
            IEnumerable<LayoutRoadEdge>? edges)
        {
            issues.Add(new LayoutIssueDto
            {
                Code = code,
                Severity = severity,
                Message = message,
                ModuleCodes = modules?.ToList() ?? new List<string>(),
                NodeCodes = nodes?.ToList() ?? new List<string>(),
                Edges = edges?.ToList() ?? new List<LayoutRoadEdge>(),
            });
        }
    }
}
