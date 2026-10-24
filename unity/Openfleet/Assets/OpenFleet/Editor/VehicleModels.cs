using UnityEngine;
using static OpenFleet.Unity.Editor.ModelKit;
using static OpenFleet.Unity.Shapes;

namespace OpenFleet.Unity.Editor
{
    /// <summary>
    /// AGV Prefab。原點在車底中心，+Z 為車頭（VehicleView 以行進方向轉向）。
    /// 車體是 Blender 模型（Blender/Models/AGV_*.blend）；狀態燈、載貨、標籤在這裡產生，
    /// 因為 VehicleView 執行時依子物件名稱找到它們並改顏色或開關。
    /// </summary>
    internal static class VehicleModels
    {
        /// <summary>大型車：車床物料（單件、料箱、托盤），頂部為頂升平台。</summary>
        public static GameObject BuildLarge()
        {
            var root = new GameObject("AGV_Large").transform;
            Model(root, "AGV_Large", Vector3.zero);

            var status = Group(root, VehicleView.StatusLightsName, Vector3.zero);
            Box(status, "Status Strip L", new Vector3(-0.555f, 0.28f, 0f), new Vector3(0.02f, 0.05f, 1.3f), Palette.Status);
            Box(status, "Status Strip R", new Vector3(0.555f, 0.28f, 0f), new Vector3(0.02f, 0.05f, 1.3f), Palette.Status);
            Cylinder(status, "Beacon", new Vector3(0.38f, 0.52f, -0.6f), 0.1f, 0.14f, Palette.Status);

            // 載貨時才顯示：一個料箱加上裡面的工件。件數由標籤文字表示，物件本身不隨數量變化。
            var load = Group(root, VehicleView.LoadName, new Vector3(0f, 0.45f, -0.05f));
            Box(load, "Bin", new Vector3(0f, 0.17f, 0f), new Vector3(0.85f, 0.34f, 1.1f), Palette.Bin);
            Box(load, "Parts", new Vector3(0f, 0.35f, 0f), new Vector3(0.75f, 0.04f, 1.0f), Palette.Steel);
            load.gameObject.SetActive(false);

            Label(root, "agv", new Vector3(0f, 1.15f, 0f), 0.05f, Color.white);
            return Finish(root);
        }

        /// <summary>小型車：只配送刀具，頂部為刀把架。</summary>
        public static GameObject BuildSmall()
        {
            var root = new GameObject("AGV_Small").transform;
            Model(root, "AGV_Small", Vector3.zero);

            var status = Group(root, VehicleView.StatusLightsName, Vector3.zero);
            Box(status, "Status Strip L", new Vector3(-0.28f, 0.22f, 0f), new Vector3(0.02f, 0.04f, 0.6f), Palette.Status);
            Box(status, "Status Strip R", new Vector3(0.28f, 0.22f, 0f), new Vector3(0.02f, 0.04f, 0.6f), Palette.Status);
            Cylinder(status, "Beacon", new Vector3(0.19f, 0.44f, -0.34f), 0.07f, 0.1f, Palette.Status);

            Label(root, "agv", new Vector3(0f, 0.75f, 0f), 0.04f, Color.white);
            return Finish(root);
        }

        /// <summary>後端設定裡沒有車型的車輛：用明顯不同的顏色提醒設定缺漏。</summary>
        public static GameObject BuildUnknown()
        {
            var root = new GameObject("AGV_Unknown").transform;
            Model(root, "AGV_Unknown", Vector3.zero);

            var status = Group(root, VehicleView.StatusLightsName, Vector3.zero);
            Box(status, "Status Strip L", new Vector3(-0.405f, 0.3f, 0f), new Vector3(0.02f, 0.05f, 0.9f), Palette.Status);
            Box(status, "Status Strip R", new Vector3(0.405f, 0.3f, 0f), new Vector3(0.02f, 0.05f, 0.9f), Palette.Status);
            Cylinder(status, "Beacon", new Vector3(0f, 0.53f, -0.35f), 0.1f, 0.14f, Palette.Status);

            Label(root, "agv", new Vector3(0f, 0.9f, 0f), 0.05f, Color.white);
            return Finish(root);
        }

        /// <summary>VehicleView 不需要設定欄位：燈條、標籤、貨物在執行時依子物件名稱找到，車型由建立它的 PresentationController 指定。</summary>
        private static GameObject Finish(Transform root)
        {
            root.gameObject.AddComponent<VehicleView>();
            return root.gameObject;
        }
    }
}
