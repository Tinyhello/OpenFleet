using System;
using System.Collections.Generic;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>
    /// 驗證結果整理成編輯器上色用的集合：錯誤的節點、路段、模組畫紅色，警告的節點畫黃色。
    /// 直接依各問題的嚴重度取 NodeCodes／Edges／ModuleCodes：edge-short、edge-crossing、edge-clearance、edge-through-module、
    /// edge-invalid、edge-outside、handoff-missing、unreachable 是錯誤；node-isolated、handoff-through 是警告。
    /// 路段不分方向（A–B 與 B–A 視為同一條）。
    /// </summary>
    public sealed class LayoutIssueMarks
    {
        private readonly HashSet<string> _errorEdges = new HashSet<string>(StringComparer.Ordinal);

        public HashSet<string> ErrorNodeCodes { get; } = new HashSet<string>(StringComparer.Ordinal);

        public HashSet<string> WarningNodeCodes { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>錯誤的外牆編號（floor-invalid、gate-invalid 等問題的 WallIndices），編輯器把這些牆畫紅。</summary>
        public HashSet<int> ErrorWallIndices { get; } = new HashSet<int>();

        public HashSet<string> ErrorModuleCodes { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>錯誤的路段（已把兩端排序），查詢用 <see cref="HasErrorEdge"/>。</summary>
        public IReadOnlyCollection<string> ErrorEdgeKeys => _errorEdges;

        /// <summary>這條路段是否被標成錯誤（不分方向）。</summary>
        public bool HasErrorEdge(string from, string to) => _errorEdges.Contains(EdgeKey(from, to));

        /// <summary>路段的比對鍵：兩端代碼依序排列，A–B 與 B–A 相同。</summary>
        public static string EdgeKey(string from, string to) =>
            string.CompareOrdinal(from, to) <= 0 ? from + "|" + to : to + "|" + from;

        public static LayoutIssueMarks From(LayoutValidationDto? validation)
        {
            var marks = new LayoutIssueMarks();
            if (validation == null)
            {
                return marks;
            }

            foreach (var issue in validation.Issues)
            {
                var isError = issue.Severity == LayoutIssueSeverity.Error;
                foreach (var node in issue.NodeCodes)
                {
                    (isError ? marks.ErrorNodeCodes : marks.WarningNodeCodes).Add(node);
                }

                if (!isError)
                {
                    continue;
                }

                if (issue.WallIndices != null)
                {
                    foreach (var wall in issue.WallIndices)
                    {
                        marks.ErrorWallIndices.Add(wall);
                    }
                }

                foreach (var module in issue.ModuleCodes)
                {
                    marks.ErrorModuleCodes.Add(module);
                }

                foreach (var edge in issue.Edges)
                {
                    marks._errorEdges.Add(EdgeKey(edge.From, edge.To));
                }
            }

            return marks;
        }
    }
}
