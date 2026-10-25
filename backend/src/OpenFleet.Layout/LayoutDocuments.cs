using System.Globalization;
using System.Linq;
using System.Text;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>
    /// 佈局文件的複製與比較。共用程式庫不能用 System.Text.Json（Unity 沒有），所以手寫；
    /// 欄位新增時兩個方法都要跟著改（測試用反射檢查屬性數量，漏改會失敗）。
    /// </summary>
    public static class LayoutDocuments
    {
        /// <summary>深複製。</summary>
        public static FactoryLayoutDocument Clone(FactoryLayoutDocument source)
        {
            var copy = new FactoryLayoutDocument
            {
                Floor = new LayoutFloor
                {
                    MinX = source.Floor.MinX,
                    MaxX = source.Floor.MaxX,
                    MinZ = source.Floor.MinZ,
                    MaxZ = source.Floor.MaxZ,
                    Outline = source.Floor.Outline?.Select(p => p == null ? null! : new LayoutPoint { X = p.X, Z = p.Z }).ToList(),
                    Gate = source.Floor.Gate == null ? null : new LayoutPoint { X = source.Floor.Gate.X, Z = source.Floor.Gate.Z },
                },
            };
            foreach (var module in source.Modules)
            {
                copy.Modules.Add(Clone(module));
            }

            foreach (var node in source.RoadNodes)
            {
                copy.RoadNodes.Add(new LayoutRoadNode { Code = node.Code, X = node.X, Z = node.Z });
            }

            foreach (var edge in source.RoadEdges)
            {
                copy.RoadEdges.Add(new LayoutRoadEdge { From = edge.From, To = edge.To });
            }

            return copy;
        }

        /// <summary>複製一個模組。</summary>
        public static LayoutModule Clone(LayoutModule module) =>
            new LayoutModule { Code = module.Code, Kind = module.Kind, X = module.X, Z = module.Z, Rotation = module.Rotation };

        /// <summary>
        /// 文件的標準字串（順序有意義、數字用 invariant 的來回格式）：兩份文件的字串相同就是完全相同。
        /// 用來判斷「有沒有未儲存的修改」與「這次操作有沒有改到東西」。
        /// </summary>
        public static string Canonical(FactoryLayoutDocument document)
        {
            var text = new StringBuilder();
            var floor = document.Floor;
            text.Append("F ").Append(Number(floor.MinX)).Append(' ').Append(Number(floor.MaxX)).Append(' ')
                .Append(Number(floor.MinZ)).Append(' ').Append(Number(floor.MaxZ)).Append('\n');
            if (floor.Outline != null)
            {
                text.Append('O');
                foreach (var point in floor.Outline)
                {
                    text.Append(' ');
                    if (point == null)
                    {
                        text.Append("null");
                    }
                    else
                    {
                        text.Append(Number(point.X)).Append(' ').Append(Number(point.Z));
                    }
                }

                text.Append("\n");
            }

            if (floor.Gate != null)
            {
                text.Append("G ").Append(Number(floor.Gate.X)).Append(' ').Append(Number(floor.Gate.Z)).Append("\n");
            }

            foreach (var module in document.Modules)
            {
                text.Append("M ").Append(module.Code).Append(' ').Append((int)module.Kind).Append(' ')
                    .Append(Number(module.X)).Append(' ').Append(Number(module.Z)).Append(' ').Append(module.Rotation).Append('\n');
            }

            foreach (var node in document.RoadNodes)
            {
                text.Append("N ").Append(node.Code).Append(' ').Append(Number(node.X)).Append(' ').Append(Number(node.Z)).Append('\n');
            }

            foreach (var edge in document.RoadEdges)
            {
                text.Append("E ").Append(edge.From).Append(' ').Append(edge.To).Append('\n');
            }

            return text.ToString();
        }

        private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
