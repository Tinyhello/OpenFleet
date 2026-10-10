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
            go.transform.SetPositionAndRotation(new Vector3(0f, 0f, 1f) - rotation * Vector3.forward * 42f, rotation);
            return go.AddComponent<FactoryCameraController>();
        }

        private static void BuildEnvironment(Transform env)
        {
            var size = FactoryLayout.FloorSize;
            Box(env, "Floor", new Vector3(0f, -0.05f, 0f), new Vector3(size.x, 0.1f, size.y), Palette.Floor);

            // 牆與柱子
            const float wallHeight = 1.2f, t = 0.2f;
            Box(env, "Wall N", new Vector3(0f, wallHeight / 2f, size.y / 2f), new Vector3(size.x + t, wallHeight, t), Palette.Wall);
            Box(env, "Wall S", new Vector3(0f, wallHeight / 2f, -size.y / 2f), new Vector3(size.x + t, wallHeight, t), Palette.Wall);
            Box(env, "Wall E", new Vector3(size.x / 2f, wallHeight / 2f, 0f), new Vector3(t, wallHeight, size.y), Palette.Wall);
            Box(env, "Wall W", new Vector3(-size.x / 2f, wallHeight / 2f, 0f), new Vector3(t, wallHeight, size.y), Palette.Wall);
            for (var x = -size.x / 2f; x <= size.x / 2f + 0.01f; x += 8f)
            {
                foreach (var z in new[] { -size.y / 2f, size.y / 2f })
                {
                    Box(env, "Pillar", new Vector3(x, 2f, z), new Vector3(0.5f, 4f, 0.5f), Palette.Pillar);
                }
            }

            // 環狀走道：兩條車道共用一片鋪面，外緣與內緣是黃線，兩條車道之間是白色分隔線。
            // 沒有畫方向箭頭：車道不限方向，由後端的路段預約決定誰先走。
            var outer = FactoryLayout.OuterLane + new Vector2(FactoryLayout.LaneEdge, FactoryLayout.LaneEdge);
            var inner = FactoryLayout.InnerLane - new Vector2(FactoryLayout.InnerLaneEdge, FactoryLayout.InnerLaneEdge);
            var mid = (outer + inner) / 2f;
            var width = outer - inner;
            var aisle = Group(env, "Aisle", Vector3.zero);
            Box(aisle, "South", new Vector3(0f, 0.005f, -mid.y), new Vector3(outer.x * 2f, 0.01f, width.y), Palette.Aisle);
            Box(aisle, "North", new Vector3(0f, 0.005f, mid.y), new Vector3(outer.x * 2f, 0.01f, width.y), Palette.Aisle);
            Box(aisle, "West", new Vector3(-mid.x, 0.005f, 0f), new Vector3(width.x, 0.01f, inner.y * 2f), Palette.Aisle);
            Box(aisle, "East", new Vector3(mid.x, 0.005f, 0f), new Vector3(width.x, 0.01f, inner.y * 2f), Palette.Aisle);

            FloorFrame(aisle, "Outer Line", Vector3.zero, outer * 2f, 0.1f, Palette.LaneLine);
            FloorFrame(aisle, "Inner Line", Vector3.zero, inner * 2f, 0.1f, Palette.LaneLine);
            var divider = (FactoryLayout.OuterLane + FactoryLayout.InnerLane) / 2f;
            FloorFrame(aisle, "Lane Divider", Vector3.zero, divider * 2f, 0.05f, Palette.ZoneLine);
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

        private static void BuildChargingArea(Transform root)
        {
            FloorFrame(root, "Zone", Vector3.zero, new Vector2(7.6f, 3.6f), 0.06f, Palette.ChargerLight);
            foreach (var x in new[] { -2.3f, 0f, 2.3f })
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
