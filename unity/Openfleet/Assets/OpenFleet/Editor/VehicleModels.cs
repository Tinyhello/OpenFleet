using UnityEngine;
using static OpenFleet.Unity.Editor.ModelKit;

namespace OpenFleet.Unity.Editor
{
    /// <summary>AGV 模型。原點在車底中心，+Z 為車頭（VehicleView 以行進方向轉向）。</summary>
    internal static class VehicleModels
    {
        /// <summary>大型車：車床物料（單件、料箱、托盤），頂部為頂升平台。</summary>
        public static GameObject BuildLarge()
        {
            var root = new GameObject("AGV_Large").transform;
            var status = Group(root, VehicleView.StatusLightsName, Vector3.zero);

            Box(root, "Chassis", new Vector3(0f, 0.24f, 0f), new Vector3(1.1f, 0.3f, 1.6f), Palette.AgvChassis);
            Box(root, "Lift Deck", new Vector3(0f, 0.42f, 0f), new Vector3(1.0f, 0.06f, 1.4f), Palette.AgvLargeDeck);
            Box(root, "Bumper", new Vector3(0f, 0.18f, 0.82f), new Vector3(1.0f, 0.1f, 0.05f), Palette.LaneLine);
            Box(root, "Bumper", new Vector3(0f, 0.18f, -0.82f), new Vector3(1.0f, 0.1f, 0.05f), Palette.LaneLine);
            Cylinder(root, "Lidar", new Vector3(0f, 0.5f, 0.68f), 0.14f, 0.1f, Palette.Sensor);
            Wheels(root, 0.5f, 0.55f, 0.24f, 0.1f);

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
            var status = Group(root, VehicleView.StatusLightsName, Vector3.zero);

            Box(root, "Body", new Vector3(0f, 0.2f, 0f), new Vector3(0.55f, 0.26f, 0.8f), Palette.AgvSmallBody);
            Box(root, "Rim", new Vector3(0f, 0.34f, 0f), new Vector3(0.56f, 0.03f, 0.81f), Palette.AgvSmallAccent);
            Box(root, "Tool Tray", new Vector3(0f, 0.37f, -0.06f), new Vector3(0.45f, 0.03f, 0.52f), Palette.Steel);
            for (var ix = 0; ix < 2; ix++)
            {
                for (var iz = 0; iz < 3; iz++)
                {
                    Cylinder(root, "Tool Holder", new Vector3(-0.1f + ix * 0.2f, 0.45f, -0.22f + iz * 0.16f), 0.07f, 0.14f, Palette.MachineDark);
                }
            }

            Cylinder(root, "Lidar", new Vector3(0f, 0.4f, 0.32f), 0.1f, 0.06f, Palette.Sensor);
            Wheels(root, 0.26f, 0.27f, 0.16f, 0.06f);

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
            var status = Group(root, VehicleView.StatusLightsName, Vector3.zero);

            Box(root, "Body", new Vector3(0f, 0.25f, 0f), new Vector3(0.8f, 0.4f, 1.1f), Palette.AgvUnknown);
            Cylinder(root, "Lidar", new Vector3(0f, 0.5f, 0.45f), 0.12f, 0.08f, Palette.Sensor);
            Wheels(root, 0.38f, 0.38f, 0.18f, 0.08f);
            Box(status, "Status Strip L", new Vector3(-0.405f, 0.3f, 0f), new Vector3(0.02f, 0.05f, 0.9f), Palette.Status);
            Box(status, "Status Strip R", new Vector3(0.405f, 0.3f, 0f), new Vector3(0.02f, 0.05f, 0.9f), Palette.Status);
            Cylinder(status, "Beacon", new Vector3(0f, 0.53f, -0.35f), 0.1f, 0.14f, Palette.Status);

            Label(root, "agv", new Vector3(0f, 0.9f, 0f), 0.05f, Color.white);
            return Finish(root);
        }

        private static void Wheels(Transform root, float x, float z, float diameter, float width)
        {
            foreach (var sx in new[] { -x, x })
            {
                foreach (var sz in new[] { -z, z })
                {
                    Cylinder(root, "Wheel", new Vector3(sx, diameter / 2f, sz), diameter, width, Palette.Rubber, new Vector3(0f, 0f, 90f));
                }
            }
        }

        /// <summary>VehicleView 不需要設定欄位：燈條、標籤、貨物在執行時依子物件名稱找到，車型由建立它的 PresentationController 指定。</summary>
        private static GameObject Finish(Transform root)
        {
            root.gameObject.AddComponent<VehicleView>();
            return root.gameObject;
        }
    }
}
