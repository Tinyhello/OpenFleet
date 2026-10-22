using UnityEngine;
using static OpenFleet.Unity.Editor.ModelKit;

namespace OpenFleet.Unity.Editor
{
    /// <summary>
    /// 各站點模型。區域座標：+Z 朝走道（交接點那側），占地約 X ±2.8、Z −2 ~ +2。
    /// 設備本體是 Blender 模型（Blender/Models/Station_*.blend，一種站一個檔）；
    /// 地面框線、執行時會切換的料箱、文字標籤在這裡產生。
    /// 模型只表示「這裡是哪種站」，不代表機台數量或實際設備規格。
    /// </summary>
    internal static class StationModels
    {
        public static void Build(StationKind kind, Transform root)
        {
            switch (kind)
            {
                case StationKind.Cutting: Cutting(root); break;
                case StationKind.RawMaterialStore: RawMaterialStore(root); break;
                case StationKind.RoughMachining: Lathe(root, Palette.RoughAccent); break;
                case StationKind.FineMachining: Lathe(root, Palette.FineAccent); break;
                case StationKind.HeatTreatment: Model(root, "Station_HeatTreatment", Vector3.zero); break;
                case StationKind.Plating: Model(root, "Station_Plating", Vector3.zero); break;
                case StationKind.Polishing: Model(root, "Station_Polishing", Vector3.zero); break;
                case StationKind.QualityInspection: Model(root, "Station_QualityInspection", Vector3.zero); break;
                case StationKind.Packing: Model(root, "Station_Packing", Vector3.zero); break;
                case StationKind.Shipping: Shipping(root); break;
                case StationKind.ToolStore: Model(root, "Station_ToolStore", Vector3.zero); break;
            }
        }

        private static void RawMaterialStore(Transform root)
        {
            Model(root, "Station_RawMaterialStore", Vector3.zero);
            // 出庫暫存區
            FloorFrame(root, "Staging", new Vector3(0f, 0f, 1.3f), new Vector2(4.8f, 1.3f), 0.06f, Palette.ZoneLine);
        }

        /// <summary>CNC 車床加上機邊備料台、成品台。粗加工與精加工共用模型，只換強調色。</summary>
        private static void Lathe(Transform root, Material accent)
        {
            Model(root, "Station_CncLathe", Vector3.zero, 0f, (Palette.RoughAccent, accent));

            // 備料區（待加工）與成品區（已加工、等待搬走）：台子在模型裡，台上的料箱只在有料時顯示（StationAnchor 切換）。
            // 容量與數量在後端，畫面上用進度條表示，沒有逐件的動畫。
            var inbound = Group(root, "Inbound Table", new Vector3(2.1f, 0f, 1.3f));
            var inboundContents = Group(inbound, StationAnchor.InboundContentsName, Vector3.zero);
            Box(inboundContents, "Bin", new Vector3(0f, 0.83f, 0f), new Vector3(0.6f, 0.26f, 0.42f), Palette.Bin);
            Cylinder(inboundContents, "Blank", new Vector3(-0.12f, 0.99f, 0f), 0.1f, 0.34f, Palette.MachineDark, new Vector3(90f, 0f, 0f));
            Cylinder(inboundContents, "Blank", new Vector3(0.12f, 0.99f, 0f), 0.1f, 0.34f, Palette.MachineDark, new Vector3(90f, 0f, 0f));
            Label(inbound, "備料", new Vector3(0f, 1.25f, 0f), 0.045f, Color.white);

            var outbound = Group(root, "Outbound Table", new Vector3(-2.1f, 0f, 1.3f));
            var outboundContents = Group(outbound, StationAnchor.OutboundContentsName, Vector3.zero);
            Box(outboundContents, "Bin", new Vector3(0f, 0.83f, 0f), new Vector3(0.6f, 0.26f, 0.42f), Palette.Bin);
            Cylinder(outboundContents, "Part", new Vector3(-0.12f, 0.99f, 0f), 0.1f, 0.34f, Palette.Steel, new Vector3(90f, 0f, 0f));
            Cylinder(outboundContents, "Part", new Vector3(0.12f, 0.99f, 0f), 0.1f, 0.34f, Palette.Steel, new Vector3(90f, 0f, 0f));
            Label(outbound, "成品", new Vector3(0f, 1.25f, 0f), 0.045f, Color.white);
        }

        /// <summary>
        /// 切料站：臥式帶鋸機。機邊兩個台子和車床同一個位置：右邊是待切的圓棒、左邊是切好的備料，
        /// 台上的料只在有料時顯示（StationAnchor 切換）。
        /// </summary>
        private static void Cutting(Transform root)
        {
            Model(root, "Station_Cutting", Vector3.zero);

            var inbound = Group(root, "Inbound Table", new Vector3(2.1f, 0f, 1.3f));
            var inboundContents = Group(inbound, StationAnchor.InboundContentsName, Vector3.zero);
            foreach (var z in new[] { -0.12f, 0f, 0.12f })
            {
                Cylinder(inboundContents, "Bar", new Vector3(0f, 0.74f, z), 0.08f, 0.95f, Palette.Steel, new Vector3(0f, 0f, 90f));
            }

            Label(inbound, "圓棒", new Vector3(0f, 1.25f, 0f), 0.045f, Color.white);

            var outbound = Group(root, "Outbound Table", new Vector3(-2.1f, 0f, 1.3f));
            var outboundContents = Group(outbound, StationAnchor.OutboundContentsName, Vector3.zero);
            Box(outboundContents, "Bin", new Vector3(0f, 0.83f, 0f), new Vector3(0.6f, 0.26f, 0.42f), Palette.Bin);
            Cylinder(outboundContents, "Blank", new Vector3(-0.12f, 0.99f, 0f), 0.1f, 0.2f, Palette.Steel, new Vector3(90f, 0f, 0f));
            Cylinder(outboundContents, "Blank", new Vector3(0.12f, 0.99f, 0f), 0.1f, 0.2f, Palette.Steel, new Vector3(90f, 0f, 0f));
            Label(outbound, "備料", new Vector3(0f, 1.25f, 0f), 0.045f, Color.white);
        }

        private static void Shipping(Transform root)
        {
            FloorFrame(root, "Dock Zone", new Vector3(0f, 0f, 0.2f), new Vector2(5.0f, 3.2f), 0.08f, Palette.LaneLine);
            Model(root, "Station_Shipping", Vector3.zero);
        }
    }
}
