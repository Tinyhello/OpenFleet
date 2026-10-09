using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using static OpenFleet.Unity.Editor.ModelKit;

namespace OpenFleet.Unity.Editor
{
    /// <summary>
    /// 產生材質、AGV Prefab 與工廠場景。模型全部由程式組裝，修改後重新執行選單即可重建。
    /// 注意：會覆寫 Factory.unity 與 Prefabs，手動修改會被蓋掉。
    /// </summary>
    internal static class FactorySceneBuilder
    {
        private const string Root = "Assets/OpenFleet";
        private const string PrefabFolder = Root + "/Prefabs";
        internal const string ScenePath = Root + "/Scenes/Factory.unity";
        private const string UiFolder = Root + "/UI";

        [MenuItem("OpenFleet/Build Factory Scene")]
        public static void Build()
        {
            EnsureFolder(Palette.Folder);
            EnsureFolder(PrefabFolder);

            var largePath = SavePrefab(VehicleModels.BuildLarge());
            var smallPath = SavePrefab(VehicleModels.BuildSmall());
            var unknownPath = SavePrefab(VehicleModels.BuildUnknown());

            var panelPath = SavePanelSettings();

            // NewScene 會卸載未使用的資源，之前拿到的 Prefab 參考會失效，所以開新場景後再依路徑載入。
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var large = AssetDatabase.LoadAssetAtPath<VehicleView>(largePath);
            var small = AssetDatabase.LoadAssetAtPath<VehicleView>(smallPath);
            var unknown = AssetDatabase.LoadAssetAtPath<VehicleView>(unknownPath);
            SetupLighting();

            var factory = new GameObject("Factory").transform;
            BuildEnvironment(Group(factory, "Environment", Vector3.zero));
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

            var fleet = new GameObject("OpenFleet");
            var view = fleet.AddComponent<FleetView>();
            var so = new SerializedObject(view);
            so.FindProperty("largeVehiclePrefab").objectReferenceValue = large;
            so.FindProperty("smallVehiclePrefab").objectReferenceValue = small;
            so.FindProperty("unknownVehiclePrefab").objectReferenceValue = unknown;
            so.FindProperty("cameraController").objectReferenceValue = controller;
            so.ApplyModifiedPropertiesWithoutUndo();
            if (large == null || small == null || unknown == null)
            {
                Debug.LogError("[OpenFleet] AGV prefabs could not be loaded; FleetView references are missing.");
            }

            // P2 操作面板（UI Toolkit）。介面由 ProductionPanel 用程式建立，這裡只提供 PanelSettings。
            var document = fleet.AddComponent<UnityEngine.UIElements.UIDocument>();
            document.panelSettings = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(panelPath);
            var panel = fleet.AddComponent<ProductionPanel>();
            var panelObject = new SerializedObject(panel);
            panelObject.FindProperty("fleetView").objectReferenceValue = view;
            panelObject.ApplyModifiedPropertiesWithoutUndo();
            if (document.panelSettings == null || document.panelSettings.themeStyleSheet == null)
            {
                Debug.LogError("[OpenFleet] UI Toolkit panel settings or theme could not be loaded; the production panel will not render.");
            }

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

            // 環狀主走道：鋪面 + 兩側黃線。沒有畫方向箭頭，因為交通規則（單向／雙向）尚未決定。
            var loop = FactoryLayout.AisleLoop;
            var min = loop[0];
            var max = loop[2];
            var w = FactoryLayout.AisleWidth;
            var aisle = Group(env, "Aisle", Vector3.zero);
            var lengthX = max.x - min.x + w;
            var lengthZ = max.y - min.y - w;
            Box(aisle, "South", new Vector3((min.x + max.x) / 2f, 0.005f, min.y), new Vector3(lengthX, 0.01f, w), Palette.Aisle);
            Box(aisle, "North", new Vector3((min.x + max.x) / 2f, 0.005f, max.y), new Vector3(lengthX, 0.01f, w), Palette.Aisle);
            Box(aisle, "West", new Vector3(min.x, 0.005f, (min.y + max.y) / 2f), new Vector3(w, 0.01f, lengthZ), Palette.Aisle);
            Box(aisle, "East", new Vector3(max.x, 0.005f, (min.y + max.y) / 2f), new Vector3(w, 0.01f, lengthZ), Palette.Aisle);

            var center = new Vector3((min.x + max.x) / 2f, 0f, (min.y + max.y) / 2f);
            var span = max - min;
            FloorFrame(aisle, "Outer Line", center, span + new Vector2(w, w), 0.1f, Palette.LaneLine);
            FloorFrame(aisle, "Inner Line", center, span - new Vector2(w, w), 0.1f, Palette.LaneLine);
        }

        private static void BuildStation(Transform parent, StationSpec spec)
        {
            var root = Group(parent, $"{spec.Id} ({spec.EnName})", ToWorld(spec.Position), spec.FacesSouth ? 180f : 0f);

            StationModels.Build(spec.Kind, Group(root, "Model", Vector3.zero));
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
                var bay = Group(root, "Charging Bay", new Vector3(x, 0f, 0f));
                Box(bay, "Pad", new Vector3(0f, 0.02f, 0.2f), new Vector3(1.6f, 0.04f, 2.2f), Palette.ChargerPad);
                Box(bay, "Charger", new Vector3(0f, 0.6f, -1.3f), new Vector3(0.5f, 1.2f, 0.35f), Palette.MachineBody);
                Box(bay, "Light", new Vector3(0f, 0.95f, -1.12f), new Vector3(0.3f, 0.08f, 0.02f), Palette.ChargerLight);
                Box(bay, "Contact", new Vector3(0f, 0.15f, -1.05f), new Vector3(0.3f, 0.1f, 0.15f), Palette.Steel);
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
        private static string SavePanelSettings()
        {
            EnsureFolder(UiFolder);
            var themePath = UiFolder + "/OpenFleetTheme.tss";
            File.WriteAllText(themePath, "@import url(\"unity-theme://default\");\n");
            AssetDatabase.ImportAsset(themePath, ImportAssetOptions.ForceUpdate);

            var path = UiFolder + "/OpenFleetPanel.asset";
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
            return path;
        }

        private static string SavePrefab(GameObject go)
        {
            var path = $"{PrefabFolder}/{go.name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return path;
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
