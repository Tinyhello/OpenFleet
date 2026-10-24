using System;
using OpenFleet.Contracts;
using OpenFleet.Layout;
using UnityEditor;
using UnityEngine;
using static OpenFleet.Unity.Editor.ModelKit;
using static OpenFleet.Unity.Shapes;

namespace OpenFleet.Unity.Editor
{
    /// <summary>
    /// 每種模組（<see cref="ModuleKind"/>）一個 Prefab：Resources/Prefabs/Modules/Module_&lt;Kind&gt;.prefab。
    /// 占地、交接點位置、模型名稱與容量來自共用程式庫的 <see cref="ModuleCatalog"/>，和後端驗證用的是同一份；
    /// 這裡只負責把它們畫出來。座標是區域座標：原點在模組中心，朝向 0 時 +Z 朝走道（交接點那側）。
    ///
    /// Prefab 的結構（執行時 LayoutScene 依名稱找到這些部分）：
    /// <list type="bullet">
    /// <item><c>Model</c>：設備本體（Blender 模型或程式組裝），含執行時會切換的備料區、成品區料箱（名稱用 StationAnchor 的常數）。</item>
    /// <item><c>Zone</c>：依目錄占地畫的地面框線。</item>
    /// <item><c>Handoff Point</c>：有交接點的種類才有，位置照目錄；工站與倉儲站有青色方框，AGV 車位沒有（車就停在那個點上）。</item>
    /// <item><c>Label</c>：文字標籤（TextMesh＋Billboard），文字執行時才依佈局填。</item>
    /// </list>
    /// 外觀（尺寸、框線、標籤位置）和第 1b 步 Editor 直接產生的場景相同。
    /// </summary>
    internal static class ModulePrefabs
    {
        public static void SaveAll(string folder)
        {
            foreach (ModuleKind kind in Enum.GetValues(typeof(ModuleKind)))
            {
                var go = Build(kind);
                PrefabUtility.SaveAsPrefabAsset(go, $"{folder}/{PresentationController.ModulePrefabName(kind)}.prefab");
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        public static GameObject Build(ModuleKind kind)
        {
            var spec = ModuleCatalog.Get(kind);
            var root = new GameObject(PresentationController.ModulePrefabName(kind)).transform;

            BuildModel(kind, spec, Group(root, "Model", Vector3.zero));

            var size = new Vector2((float)spec.Width, (float)spec.Depth);
            var center = new Vector3((float)spec.OffsetX, 0f, (float)spec.OffsetZ);
            if (ModuleCatalog.IsBay(kind))
            {
                // 車位：充電維護區原本的淺藍色框線，每個車位各一個。
                FloorFrame(root, "Zone", center, size, 0.06f, Palette.ChargerLight);
            }
            else
            {
                FloorFrame(root, "Zone", center, size, kind == ModuleKind.CarrierStorage ? 0.06f : 0.05f, Palette.ZoneLine);
            }

            if (spec.HasHandoff)
            {
                // 交接點：AGV 停靠、由站點設備確認取放的位置。
                var handoff = Group(root, "Handoff Point", new Vector3((float)spec.HandoffX, 0f, (float)spec.HandoffZ));
                if (!ModuleCatalog.IsBay(kind))
                {
                    FloorFrame(handoff, "Marker", Vector3.zero, new Vector2(1.8f, 1.8f), 0.1f, Palette.Handoff);
                    Box(handoff, "Center", new Vector3(0f, 0.01f, 0f), new Vector3(0.3f, 0.02f, 0.3f), Palette.Handoff);
                }
            }

            if (ModuleCatalog.IsBay(kind))
            {
                // 只在編輯模式顯示（LayoutScene 決定）；車位排得很密，字小一點。
                Label(root, string.Empty, new Vector3(0f, 1.6f, 0.2f), 0.04f, Color.white);
            }
            else if (kind == ModuleKind.CarrierStorage)
            {
                Label(root, string.Empty, new Vector3(0f, 2.0f, 0f), 0.08f, Color.white);
            }
            else
            {
                Label(root, string.Empty, new Vector3(0f, 3.4f, -0.5f), 0.09f, Color.white);
            }

            return root.gameObject;
        }

        private static void BuildModel(ModuleKind kind, ModuleSpec spec, Transform model)
        {
            switch (kind)
            {
                case ModuleKind.Cutting: Cutting(model, spec.Model); break;
                case ModuleKind.RawMaterialStore: RawMaterialStore(model, spec.Model); break;
                case ModuleKind.RoughLathe: Lathe(model, spec.Model, Palette.RoughAccent); break;
                case ModuleKind.FineLathe: Lathe(model, spec.Model, Palette.FineAccent); break;
                case ModuleKind.ShippingDock: Shipping(model, spec.Model); break;
                case ModuleKind.CarrierStorage: Carriers(model); break;
                default: Model(model, spec.Model, Vector3.zero); break;
            }
        }

        private static void RawMaterialStore(Transform root, string model)
        {
            Model(root, model, Vector3.zero);
            // 出庫暫存區
            FloorFrame(root, "Staging", new Vector3(0f, 0f, 1.3f), new Vector2(4.8f, 1.3f), 0.06f, Palette.ZoneLine);
        }

        /// <summary>
        /// 一台小型 CNC 車床加上機邊的備料台、成品台（P7 第 1b 步：每台車床一站；使用者 2026-10-22 決定粗加工、精加工各兩台車床）。
        /// 粗加工與精加工共用模型，只換強調色。備料台在 +X（+0.62 m），成品台在 -X（-0.62 m）。
        /// </summary>
        private static void Lathe(Transform root, string model, Material accent)
        {
            Model(root, model, Vector3.zero, 0f, (Palette.RoughAccent, accent));

            // 備料區（待加工）與成品區（已加工、等待搬走）：台子在模型裡，台上的料箱只在有料時顯示（StationAnchor 切換）。
            // 容量與數量在後端，畫面上用進度條表示，沒有逐件的動畫。
            var inbound = Group(root, "Inbound Table", new Vector3(0.62f, 0f, 1.3f));
            var inboundContents = Group(inbound, StationAnchor.InboundContentsName, Vector3.zero);
            Box(inboundContents, "Bin", new Vector3(0f, 0.83f, 0f), new Vector3(0.6f, 0.26f, 0.42f), Palette.Bin);
            Cylinder(inboundContents, "Blank", new Vector3(-0.12f, 0.99f, 0f), 0.1f, 0.34f, Palette.MachineDark, new Vector3(90f, 0f, 0f));
            Cylinder(inboundContents, "Blank", new Vector3(0.12f, 0.99f, 0f), 0.1f, 0.34f, Palette.MachineDark, new Vector3(90f, 0f, 0f));
            Label(inbound, "備料", new Vector3(0f, 1.25f, 0f), 0.045f, Color.white);

            var outbound = Group(root, "Outbound Table", new Vector3(-0.62f, 0f, 1.3f));
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
        private static void Cutting(Transform root, string model)
        {
            Model(root, model, Vector3.zero);

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

        private static void Shipping(Transform root, string model)
        {
            FloorFrame(root, "Dock Zone", new Vector3(0f, 0f, 0.2f), new Vector2(5.0f, 3.2f), 0.08f, Palette.LaneLine);
            Model(root, model, Vector3.zero);
        }

        /// <summary>載具存放區：堆疊的料箱與托盤（歸還位置待定，這裡只是示意）。</summary>
        private static void Carriers(Transform root)
        {
            // 料箱（堆疊）
            for (var i = 0; i < 3; i++)
            {
                for (var level = 0; level < 3 - i % 2; level++)
                {
                    Box(root, "Bin", new Vector3(-2.6f + i * 0.75f, 0.14f + level * 0.29f, 0.3f), new Vector3(0.6f, 0.28f, 0.42f), Palette.Bin);
                }
            }

            // 托盤（堆疊）
            foreach (var x in new[] { 0.9f, 2.3f })
            {
                for (var level = 0; level < 4; level++)
                {
                    Box(root, "Pallet", new Vector3(x, 0.07f + level * 0.15f, 0.1f), new Vector3(1.1f, 0.14f, 1.1f), Palette.Pallet);
                }
            }
        }
    }
}
