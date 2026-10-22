using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using static OpenFleet.Unity.Editor.ModelKit;

namespace OpenFleet.Unity.Editor
{
    /// <summary>
    /// 產生材質、AGV Prefab 與工廠場景。設備與車體來自 Blender 模型（Assets/OpenFleet/Models），擺放與地面標線由程式組裝，修改後重新執行選單即可重建。
    /// 注意：會覆寫 Factory.unity 與 Resources 底下的 Prefab，手動修改會被蓋掉。
    /// 場景裡的腳本都不設定欄位：執行時需要的 Prefab 與 UI 設定放在 Resources，由 PresentationController 載入。
    /// </summary>
    internal static class FactorySceneBuilder
    {
        private const string Root = "Assets/OpenFleet";
        private const string ResourcesFolder = Root + "/Resources";
        private const string PrefabFolder = ResourcesFolder + "/" + PresentationController.PrefabFolder;
        internal const string ScenePath = Root + "/Scenes/Factory.unity";
        private const string PanelSettingsAsset = ResourcesFolder + "/" + PresentationController.PanelSettingsPath + ".asset";
        private const float WallHeight = 1.2f, WallThickness = 0.2f, ApronDepth = 5f;

        [MenuItem("OpenFleet/Build Factory Scene")]
        public static void Build()
        {
            EnsureFolder(Palette.Folder);
            EnsureFolder(PrefabFolder);
            // 模型依名稱對應材質：先確保材質都在，再重新匯入模型，讓 FBX 內的材質換成 Palette 材質。
            Palette.EnsureAll();
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ModelImport.Folder, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);

            // 檔名要和 PresentationController 載入時用的名稱一致。
            SavePrefab(VehicleModels.BuildLarge(), PresentationController.LargeVehiclePrefab);
            SavePrefab(VehicleModels.BuildSmall(), PresentationController.SmallVehiclePrefab);
            SavePrefab(VehicleModels.BuildUnknown(), PresentationController.UnknownVehiclePrefab);

            SavePanelSettings();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SetupLighting();

            var factory = new GameObject("Factory").transform;
            BuildEnvironment(Group(factory, "Environment", Vector3.zero));
            // 物件路徑 Factory/Stations 是 PresentationController 找站點的地方。
            var stations = Group(factory, "Stations", Vector3.zero);
            foreach (var spec in FactoryLayout.Stations)
            {
                BuildStation(stations, spec);
            }

            var storages = Group(factory, "Storages", Vector3.zero);
            foreach (var spec in FactoryLayout.Storages)
            {
                BuildStorage(storages, spec);
            }

            BuildChargingArea(Group(factory, "Charging Area", ToWorld(FactoryLayout.ChargingArea)));
            BuildCarrierArea(Group(factory, "Carrier Storage", ToWorld(FactoryLayout.CarrierArea)));
            MarkStatic(factory);

            var controller = BuildCamera();

            // 執行時由 Billboard 轉向；先對齊預設視角，Editor 裡看到的文字才不會左右相反。
            foreach (var billboard in Object.FindObjectsByType<Billboard>(FindObjectsInactive.Include))
            {
                billboard.transform.rotation = controller.transform.rotation;
            }

            // 不設定任何欄位：FleetView 會自動帶上 FleetMqttClient 與 PresentationController，
            // 操作面板與進度條由 PresentationController 在執行時建立。
            new GameObject("OpenFleet").AddComponent<FleetView>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[OpenFleet] Factory scene built: {ScenePath}");
        }

        private static void SetupLighting()
        {
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.4f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(52f, -35f, 0f);

            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.66f, 0.74f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.43f, 0.46f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.22f, 0.23f);
        }

        private static FactoryCameraController BuildCamera()
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.13f, 0.15f, 0.18f);
            camera.fieldOfView = 45f;
            camera.farClipPlane = 300f;
            go.AddComponent<AudioListener>();

            // 與 FactoryCameraController 的預設值一致，讓 Editor 裡看到的畫面和執行時相同。
            var rotation = Quaternion.Euler(50f, 0f, 0f);
            go.transform.SetPositionAndRotation(
                FactoryCameraController.DefaultTarget - rotation * Vector3.forward * FactoryCameraController.DefaultDistance, rotation);
            return go.AddComponent<FactoryCameraController>();
        }

        private static void BuildEnvironment(Transform env)
        {
            var floor = FactoryLayout.Floor;
            Box(env, "Floor", new Vector3(floor.center.x, -0.05f, floor.center.y), new Vector3(floor.width, 0.1f, floor.height), Palette.Floor);

            // 牆與柱子。西牆中間是工廠大門，進料與出貨共用。
            var wallFrom = floor.xMin - WallThickness / 2f;
            var wallTo = floor.xMax + WallThickness / 2f;
            Wall(env, "Wall N", true, floor.yMax, wallFrom, wallTo);
            Wall(env, "Wall S", true, floor.yMin, wallFrom, wallTo);
            Wall(env, "Wall E", false, floor.xMax, floor.yMin, floor.yMax);
            BuildMainGate(WallWithDoor(env, "Wall W", false, floor.xMin, floor.yMin, floor.yMax, FactoryLayout.GateZ, FactoryLayout.GateWidth));

            // 柱子排在站與站之間，不擋在站點前面。
            for (var x = floor.xMin + 2f; x <= floor.xMax + 0.01f; x += 8f)
            {
                foreach (var z in new[] { floor.yMin, floor.yMax })
                {
                    Box(env, "Pillar", new Vector3(x, 2f, z), new Vector3(0.5f, 4f, 0.5f), Palette.Pillar);
                }
            }

            // 環狀走道：兩條車道共用一片鋪面，外緣與內緣是黃線，兩條車道之間是白色分隔線。
            // 沒有畫方向箭頭：車道不限方向，由後端的路段預約決定誰先走。
            var outer = Grow(FactoryLayout.OuterLane, FactoryLayout.LaneEdge);
            var inner = Grow(FactoryLayout.InnerLane, -FactoryLayout.InnerLaneEdge);
            var aisle = Group(env, "Aisle", Vector3.zero);
            AisleStrip(aisle, "South", outer.xMin, outer.xMax, outer.yMin, inner.yMin);
            AisleStrip(aisle, "North", outer.xMin, outer.xMax, inner.yMax, outer.yMax);
            AisleStrip(aisle, "West", outer.xMin, inner.xMin, inner.yMin, inner.yMax);
            AisleStrip(aisle, "East", inner.xMax, outer.xMax, inner.yMin, inner.yMax);

            FloorFrame(aisle, "Outer Line", Center(outer), outer.size, 0.1f, Palette.LaneLine);
            FloorFrame(aisle, "Inner Line", Center(inner), inner.size, 0.1f, Palette.LaneLine);
            var lanes = (Outer: FactoryLayout.OuterLane, Inner: FactoryLayout.InnerLane);
            var divider = Rect.MinMaxRect(
                (lanes.Outer.xMin + lanes.Inner.xMin) / 2f, (lanes.Outer.yMin + lanes.Inner.yMin) / 2f,
                (lanes.Outer.xMax + lanes.Inner.xMax) / 2f, (lanes.Outer.yMax + lanes.Inner.yMax) / 2f);
            FloorFrame(aisle, "Lane Divider", Center(divider), divider.size, 0.05f, Palette.ZoneLine);
        }

        /// <summary>矩形往外擴 <paramref name="by"/>（負數往內縮）。</summary>
        private static Rect Grow(Rect rect, float by) => Rect.MinMaxRect(rect.xMin - by, rect.yMin - by, rect.xMax + by, rect.yMax + by);

        private static Vector3 Center(Rect rect) => new Vector3(rect.center.x, 0f, rect.center.y);

        /// <summary>走道鋪面的一段：X 從 <paramref name="x0"/> 到 <paramref name="x1"/>、Z 從 <paramref name="z0"/> 到 <paramref name="z1"/>。</summary>
        private static void AisleStrip(Transform aisle, string name, float x0, float x1, float z0, float z1)
        {
            Box(aisle, name, new Vector3((x0 + x1) / 2f, 0.005f, (z0 + z1) / 2f), new Vector3(x1 - x0, 0.01f, z1 - z0), Palette.Aisle);
        }

        /// <summary>
        /// 一面有出入口的牆：在 <paramref name="doorAt"/> 留一個寬 <paramref name="doorWidth"/> 的缺口，牆外鋪一塊進出用的地面。
        /// 回傳出入口的群組（+Z 朝廠外），讓呼叫端加上門柱或貨物。其他參數的意義同 <see cref="Wall"/>。
        /// </summary>
        private static Transform WallWithDoor(Transform env, string name, bool alongX, float offset, float from, float to, float doorAt, float doorWidth)
        {
            Wall(env, name + " A", alongX, offset, from, doorAt - doorWidth / 2f);
            Wall(env, name + " B", alongX, offset, doorAt + doorWidth / 2f, to);

            var position = alongX ? new Vector3(doorAt, 0f, offset) : new Vector3(offset, 0f, doorAt);
            var outward = alongX ? (offset > 0f ? 0f : 180f) : (offset > 0f ? 90f : -90f);
            var door = Group(env, name + " Door", position, outward);
            Box(door, "Apron", new Vector3(0f, -0.05f, ApronDepth / 2f), new Vector3(doorWidth, 0.1f, ApronDepth), Palette.Aisle);
            Box(door, "Threshold", new Vector3(0f, 0.01f, 0f), new Vector3(doorWidth, 0.02f, 0.3f), Palette.LaneLine);
            return door;
        }

        /// <summary>
        /// 一段牆。<paramref name="alongX"/> 為 true 時沿 X 方向、位於 Z = <paramref name="offset"/>；否則沿 Z 方向、位於 X = offset。
        /// <paramref name="from"/>／<paramref name="to"/> 是牆在延伸方向上的起訖。
        /// </summary>
        private static void Wall(Transform env, string name, bool alongX, float offset, float from, float to)
        {
            var mid = (from + to) / 2f;
            var length = to - from;
            Box(env, name,
                alongX ? new Vector3(mid, WallHeight / 2f, offset) : new Vector3(offset, WallHeight / 2f, mid),
                alongX ? new Vector3(length, WallHeight, WallThickness) : new Vector3(WallThickness, WallHeight, length),
                Palette.Wall);
        }

        /// <summary>
        /// 工廠大門（西牆中間，進料與出貨共用）：門柱、門楣、標示，以及兩側的貨物。
        /// 群組的 +Z 朝廠外、+X 朝北：門外南側是剛到廠還沒入庫的原料，
        /// 門內北側（靠成品倉那一邊）是包裝好等著出貨的成品箱。貨物只是示意，沒有對應的後端資料。
        /// </summary>
        private static void BuildMainGate(Transform gate)
        {
            var half = FactoryLayout.GateWidth / 2f;
            foreach (var x in new[] { -half, half })
            {
                Box(gate, "Post", new Vector3(x, 2.25f, 0f), new Vector3(0.5f, 4.5f, 0.5f), Palette.Pillar);
            }

            Box(gate, "Header", new Vector3(0f, 4.65f, 0f), new Vector3(FactoryLayout.GateWidth + 0.5f, 0.4f, 0.5f), Palette.LaneLine);
            Label(gate, "大門\nMain Gate", new Vector3(0f, 5f, 0f), 0.09f, Color.white);

            // 進料：門外南側。
            foreach (var x in new[] { -3.9f, -2.6f })
            {
                Box(gate, "Inbound Pallet", new Vector3(x, 0.07f, 2.4f), new Vector3(1.1f, 0.14f, 1.1f), Palette.Pallet);
                Box(gate, "Inbound Crate", new Vector3(x, 0.44f, 2.4f), new Vector3(1.0f, 0.6f, 1.0f), Palette.Cardboard);
            }

            Label(gate, "進料\nInbound", new Vector3(-3.25f, 1f, 3.6f), 0.07f, Color.white);

            // 出貨：門內北側，沿著西牆排到出貨站前面。
            for (var i = 0; i < 3; i++)
            {
                var x = half + 1.4f + i * 1.35f;
                Box(gate, "Outbound Pallet", new Vector3(x, 0.07f, -1.3f), new Vector3(1.1f, 0.14f, 1.1f), Palette.Pallet);
                for (var level = 0; level < 3 - i % 2; level++)
                {
                    Box(gate, "Outbound Box", new Vector3(x, 0.36f + level * 0.46f, -1.3f), new Vector3(1.0f, 0.44f, 1.0f), Palette.Cardboard);
                }
            }

            Label(gate, "待出貨\nOutbound", new Vector3(half + 2.75f, 1.9f, -1.3f), 0.07f, Color.white);
        }

        private static void BuildStation(Transform parent, StationSpec spec)
        {
            // 物件名稱就是站點代碼，必須和後端的站點代碼相同：執行時 StationAnchor 依名稱對應，StatusOverlay 才對得上。
            var root = Group(parent, spec.Id, ToWorld(spec.Position), spec.FacesSouth ? 180f : 0f);

            var model = Group(root, "Model", Vector3.zero);
            StationModels.Build(spec.Kind, model);
            FloorFrame(root, "Zone", new Vector3(0f, 0f, -0.1f), new Vector2(6.4f, 4.6f), 0.05f, Palette.ZoneLine);

            // 交接點：AGV 停靠、由站點設備確認取放的位置。
            var handoff = Group(root, "Handoff Point", new Vector3(0f, 0f, FactoryLayout.HandoffOffset));
            FloorFrame(handoff, "Marker", Vector3.zero, new Vector2(1.8f, 1.8f), 0.1f, Palette.Handoff);
            Box(handoff, "Center", new Vector3(0f, 0.01f, 0f), new Vector3(0.3f, 0.02f, 0.3f), Palette.Handoff);

            Label(root, $"{spec.ZhName}\n{spec.EnName}", new Vector3(0f, 3.4f, -0.5f), 0.09f, Color.white);
        }

        /// <summary>只有模型、區域框線與標示的倉儲區：沒有交接點，不放在 Factory/Stations 底下。</summary>
        private static void BuildStorage(Transform parent, StationSpec spec)
        {
            var root = Group(parent, spec.EnName, ToWorld(spec.Position), spec.FacesSouth ? 180f : 0f);
            Model(root, spec.Id, Vector3.zero);
            FloorFrame(root, "Zone", new Vector3(0f, 0f, -0.1f), new Vector2(6.4f, 4.6f), 0.05f, Palette.ZoneLine);
            Label(root, $"{spec.ZhName}\n{spec.EnName}", new Vector3(0f, 3.4f, -0.5f), 0.09f, Color.white);
        }

        private static void BuildChargingArea(Transform root)
        {
            FloorFrame(root, "Zone", Vector3.zero, new Vector2(11.0f, 3.6f), 0.06f, Palette.ChargerLight);
            foreach (var x in FactoryLayout.ChargingBayOffsets)
            {
                Model(root, "ChargingBay", new Vector3(x, 0f, 0f));
            }

            Label(root, "充電維護區\nCharging / Maintenance", new Vector3(0f, 2.2f, -1.2f), 0.08f, Color.white);
        }

        private static void BuildCarrierArea(Transform root)
        {
            FloorFrame(root, "Zone", Vector3.zero, new Vector2(7.0f, 3.4f), 0.06f, Palette.ZoneLine);

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

            Label(root, "載具存放區\nBins / Pallets", new Vector3(0f, 2.0f, 0f), 0.08f, Color.white);
        }

        private static Vector3 ToWorld(Vector2 p) => new Vector3(p.x, 0f, p.y);

        /// <summary>靜態物件可合併批次繪製；文字標籤會轉向，不能設成靜態。</summary>
        private static void MarkStatic(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.GetComponent<TextMesh>() == null)
                {
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);
                }
            }
        }

        /// <summary>
        /// 執行期的 UI Toolkit 需要 PanelSettings 與主題樣式表；主題直接引用 Unity 內建的預設執行期主題。
        /// </summary>
        private static void SavePanelSettings()
        {
            var folder = Path.GetDirectoryName(PanelSettingsAsset).Replace('\\', '/');
            EnsureFolder(folder);
            var themePath = folder + "/OpenFleetTheme.tss";
            File.WriteAllText(themePath, "@import url(\"unity-theme://default\");\n");
            AssetDatabase.ImportAsset(themePath, ImportAssetOptions.ForceUpdate);

            var path = PanelSettingsAsset;
            var settings = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(path);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<UnityEngine.UIElements.PanelSettings>();
                AssetDatabase.CreateAsset(settings, path);
            }

            settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.ThemeStyleSheet>(themePath);
            settings.scaleMode = UnityEngine.UIElements.PanelScaleMode.ConstantPixelSize;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            if (settings.themeStyleSheet == null)
            {
                Debug.LogError("[OpenFleet] UI Toolkit theme could not be loaded; the production panel will not render.");
            }
        }

        private static void SavePrefab(GameObject go, string name)
        {
            go.name = name;
            PrefabUtility.SaveAsPrefabAsset(go, $"{PrefabFolder}/{name}.prefab");
            Object.DestroyImmediate(go);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
