using System;
using System.Collections.Generic;
using System.Linq;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>
    /// 預設佈局（示範用的暫定佈局）：工站的位置與 P7 第 1b 步相同（車床之間 8 m），路網是第 1b 步的 DemoRoadNetwork。
    /// 預設佈局不能修改；要改請另存。之後的驗證與套用都以這份文件為準，不再有寫死在程式裡的站點與車輛。
    /// </summary>
    public static class DefaultLayout
    {
        // 車道中心線：外圈 X 從 -30 到 38、內圈從 -28.2 到 36.2（下排往東加長，見實作紀錄第 4 節）。
        private const double OuterZ = 7;
        private const double OuterEastX = 38;
        private const double OuterWestX = -30;
        private const double InnerZ = 5.2;
        private const double InnerEastX = 36.2;
        private const double InnerWestX = -28.2;
        private const double ParkingZ = 0.2;

        // 站點交接點離外圈車道 2.4 m：停在交接點上的車不會壓到車道。
        private const double HandoffZ = 9.4;

        // 刀具庫在走道內側的島區中央，交接點接在內圈車道下邊。
        private const double IslandX = 0;
        private const double IslandHandoffZ = -3.2;

        // 下排的站（切料、原料倉、粗加工 1～2、精加工 1～2、淬火、回火、電鍍）都在 8 m 的格線上。
        private static readonly double[] BottomColumns = { -28, -20, -12, -4, 4, 12, 20, 28, 36 };

        // 上排的站（出貨、成品倉、包裝、品管、拋光）。
        private static readonly double[] TopColumns = { -20, -12, -4, 4, 20 };

        // 充電車位（AGV 車位的停車點）。
        private static readonly double[] ParkingColumns = { -14.4, -12.6, -10.3, -8, -5.7 };

        /// <summary>建立預設佈局文件（每次呼叫都是新的物件）。</summary>
        public static FactoryLayoutDocument Build()
        {
            var document = new FactoryLayoutDocument
            {
                Floor = new LayoutFloor { MinX = -34, MaxX = 42, MinZ = -15.2, MaxZ = 15.2 },
            };

            AddModules(document);
            AddRoads(document);
            return document;
        }

        /// <summary>路網節點代碼：座標格式 x,z（最多兩位小數、invariant）。</summary>
        public static string Code(double x, double z) => FormattableString.Invariant($"{x:0.##},{z:0.##}");

        private static void AddModules(FactoryLayoutDocument document)
        {
            Add(document, ModuleKind.Cutting, "cutting-1", -28, -12.8, 0);
            Add(document, ModuleKind.RawMaterialStore, "raw-store", -20, -12.8, 0);
            Add(document, ModuleKind.RoughLathe, "rough-1", -12, -12.8, 0);
            Add(document, ModuleKind.RoughLathe, "rough-2", -4, -12.8, 0);
            Add(document, ModuleKind.FineLathe, "fine-1", 4, -12.8, 0);
            Add(document, ModuleKind.FineLathe, "fine-2", 12, -12.8, 0);
            Add(document, ModuleKind.QuenchFurnace, "heat-1", 20, -12.8, 0);
            Add(document, ModuleKind.TemperFurnace, "temper-1", 28, -12.8, 0);
            Add(document, ModuleKind.PlatingLine, "plating-1", 36, -12.8, 0);

            Add(document, ModuleKind.Polisher, "polish-1", 20, 12.8, 180);
            Add(document, ModuleKind.InspectionBench, "qc-1", 4, 12.8, 180);
            Add(document, ModuleKind.PackingBench, "packing-1", -4, 12.8, 180);
            Add(document, ModuleKind.FinishedGoodsStore, "finished-store", -12, 12.8, 180);
            Add(document, ModuleKind.ShippingDock, "shipping", -20, 12.8, 180);
            Add(document, ModuleKind.ToolStore, "tool-store", 0, 0.2, 180);

            Add(document, ModuleKind.MiscStore, "misc-1", 12, 12.8, 180);
            Add(document, ModuleKind.CarrierStorage, "carrier-1", 8, 0, 0);

            Add(document, ModuleKind.AgvBayLarge, "agv-01", -10.3, 0, 0);
            Add(document, ModuleKind.AgvBayLarge, "agv-02", -8, 0, 0);
            Add(document, ModuleKind.AgvBaySmall, "agv-03", -5.7, 0, 0);
            Add(document, ModuleKind.AgvBayLarge, "agv-04", -12.6, 0, 0);
            Add(document, ModuleKind.AgvBayLarge, "agv-05", -14.4, 0, 0);
        }

        private static void Add(FactoryLayoutDocument document, ModuleKind kind, string code, double x, double z, int rotation)
        {
            document.Modules.Add(new LayoutModule { Kind = kind, Code = code, X = x, Z = z, Rotation = rotation });
        }

        // 第 1b 步的 DemoRoadNetwork：外圈與內圈各是一個環，內外之間每 4 m 一段連接，站點交接點與車位是只有一個出入口的支線。
        private static void AddRoads(FactoryLayoutDocument document)
        {
            var nodes = new Dictionary<string, LayoutRoadNode>(StringComparer.Ordinal);
            var order = new List<LayoutRoadNode>();
            var edgeKeys = new HashSet<string>(StringComparer.Ordinal);

            string AddNode(double x, double z)
            {
                var code = Code(x, z);
                if (!nodes.ContainsKey(code))
                {
                    var node = new LayoutRoadNode { Code = code, X = x, Z = z };
                    nodes[code] = node;
                    order.Add(node);
                }

                return code;
            }

            void Link(string a, string b)
            {
                var key = string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
                if (edgeKeys.Add(key))
                {
                    document.RoadEdges.Add(new LayoutRoadEdge { From = a, To = b });
                }
            }

            // 把一圈節點依序相連，最後接回第一個。
            void Ring(List<string> ring)
            {
                for (var i = 0; i < ring.Count; i++)
                {
                    Link(ring[i], ring[(i + 1) % ring.Count]);
                }
            }

            // 外圈：上下兩邊每 4 m 一個節點（站點的交接點都在節點上），兩側各兩個。
            var outerXs = new double[] { -28, -24, -20, -16, -12, -8, -4, 0, 4, 8, 12, 16, 20, 24, 28, 32, 36 };
            var outer = new List<string> { AddNode(OuterWestX, -OuterZ) };
            outer.AddRange(outerXs.Select(x => AddNode(x, -OuterZ)));
            outer.AddRange(new[] { AddNode(OuterEastX, -OuterZ), AddNode(OuterEastX, -2), AddNode(OuterEastX, 2), AddNode(OuterEastX, OuterZ) });
            outer.AddRange(outerXs.Reverse().Select(x => AddNode(x, OuterZ)));
            outer.AddRange(new[] { AddNode(OuterWestX, OuterZ), AddNode(OuterWestX, 2), AddNode(OuterWestX, -2) });
            Ring(outer);

            // 內圈：轉角兼作最外側兩站的連接點；上邊有充電車位的出入口。
            var innerBottomXs = new double[] { -24, -20, -16, -12, -8, -4, 0, 4, 8, 12, 16, 20, 24, 28, 32 };
            var innerTopXs = new double[] { 32, 28, 24, 20, 16, 12, 8, 4, 0, -5.7, -8, -10.3, -12.6, -14.4, -16, -20, -24 };
            var inner = new List<string> { AddNode(InnerWestX, -InnerZ) };
            inner.AddRange(innerBottomXs.Select(x => AddNode(x, -InnerZ)));
            inner.AddRange(new[] { AddNode(InnerEastX, -InnerZ), AddNode(InnerEastX, 0), AddNode(InnerEastX, InnerZ) });
            inner.AddRange(innerTopXs.Select(x => AddNode(x, InnerZ)));
            inner.AddRange(new[] { AddNode(InnerWestX, InnerZ), AddNode(InnerWestX, 0) });
            Ring(inner);

            // 內外車道之間的連接路段：兩端的節點都存在才連。
            foreach (var side in new[] { -1d, 1d })
            {
                foreach (var x in innerBottomXs)
                {
                    if (nodes.ContainsKey(Code(x, side * InnerZ)) && nodes.ContainsKey(Code(x, side * OuterZ)))
                    {
                        Link(Code(x, side * OuterZ), Code(x, side * InnerZ));
                    }
                }

                Link(Code(-28, side * OuterZ), Code(InnerWestX, side * InnerZ));
                Link(Code(36, side * OuterZ), Code(InnerEastX, side * InnerZ));
            }

            // 站點交接點：接在外圈車道上的支線。
            foreach (var x in BottomColumns)
            {
                Link(AddNode(x, -HandoffZ), Code(x, -OuterZ));
            }

            foreach (var x in TopColumns)
            {
                Link(AddNode(x, HandoffZ), Code(x, OuterZ));
            }

            // 刀具庫：接在內圈車道下邊的支線。
            Link(AddNode(IslandX, IslandHandoffZ), Code(IslandX, -InnerZ));

            // 充電車位：接在內圈車道上邊的支線。
            foreach (var x in ParkingColumns)
            {
                Link(AddNode(x, ParkingZ), Code(x, InnerZ));
            }

            document.RoadNodes.AddRange(order);
        }
    }
}
