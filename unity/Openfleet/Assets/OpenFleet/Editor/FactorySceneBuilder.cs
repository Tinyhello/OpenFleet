using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace OpenFleet.Unity.Editor
{
    /// <summary>
    /// 產生執行時要用的資源與只有最少物件的場景。設備與車體來自 Blender 模型（Assets/OpenFleet/Models），
    /// 修改後重新執行選單即可重建。注意：會覆寫 Factory.unity 與 Resources 底下的資源，手動修改會被蓋掉。
    ///
    /// 產生的內容：
    /// 1. 材質（<see cref="Palette"/>）、AGV Prefab、每種模組一個的 Prefab（<see cref="ModulePrefabs"/>）、UI 的 PanelSettings。
    /// 2. <c>Resources/Layout/LayoutMaterials.asset</c>：執行時建立環境與路網要用的材質清單。
    /// 3. 場景 Factory.unity 只留 Sun、Main Camera 與 OpenFleet 物件；廠房、模組、路網都由 LayoutController 依後端的佈局在執行時建立。
    /// 場景裡的腳本都不設定欄位：執行時需要的 Prefab 與材質放在 Resources，由 PresentationController 載入。
    /// </summary>
    internal static class FactorySceneBuilder
    {
        private const string Root = "Assets/OpenFleet";
        private const string ResourcesFolder = Root + "/Resources";
        private const string PrefabFolder = ResourcesFolder + "/" + PresentationController.PrefabFolder;
        private const string ModulePrefabFolder = ResourcesFolder + "/" + PresentationController.ModulePrefabFolder;
        private const string LayoutMaterialsAsset = ResourcesFolder + "/" + PresentationController.LayoutMaterialsPath + ".asset";
        internal const string ScenePath = Root + "/Scenes/Factory.unity";
        private const string PanelSettingsAsset = ResourcesFolder + "/" + PresentationController.PanelSettingsPath + ".asset";

        [MenuItem("OpenFleet/Build Factory Scene")]
        public static void Build()
        {
            EnsureFolder(Palette.Folder);
            EnsureFolder(PrefabFolder);
            EnsureFolder(ModulePrefabFolder);
            // 模型依名稱對應材質：先確保材質都在，再重新匯入模型，讓 FBX 內的材質換成 Palette 材質。
            Palette.EnsureAll();
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ModelImport.Folder, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);

            // 檔名要和 PresentationController 載入時用的名稱一致。
            SavePrefab(VehicleModels.BuildLarge(), PresentationController.LargeVehiclePrefab);
            SavePrefab(VehicleModels.BuildSmall(), PresentationController.SmallVehiclePrefab);
            SavePrefab(VehicleModels.BuildUnknown(), PresentationController.UnknownVehiclePrefab);
            ModulePrefabs.SaveAll(ModulePrefabFolder);

            SaveLayoutMaterials();
            SavePanelSettings();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SetupLighting();
            BuildCamera();

            // 不設定任何欄位：FleetView 會自動帶上 FleetMqttClient、PresentationController 與 LayoutController，
            // 操作面板與進度條由 PresentationController 在執行時建立，廠房由 LayoutController 依後端的佈局建立。
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

            // 與 FactoryCameraController 的預設值一致；載入佈局後由 LayoutController 依廠房大小換掉。
            var rotation = Quaternion.Euler(50f, 0f, 0f);
            go.transform.SetPositionAndRotation(
                FactoryCameraController.DefaultTarget - rotation * Vector3.forward * FactoryCameraController.DefaultDistance, rotation);
            return go.AddComponent<FactoryCameraController>();
        }

        /// <summary>
        /// 執行時建環境與路網用的材質清單。欄位由這裡依 Palette 填入，不手動設定；
        /// 放在 Resources，Player 建置才會包含這些材質。
        /// </summary>
        private static void SaveLayoutMaterials()
        {
            var folder = Path.GetDirectoryName(LayoutMaterialsAsset).Replace('\\', '/');
            EnsureFolder(folder);

            var materials = AssetDatabase.LoadAssetAtPath<LayoutMaterials>(LayoutMaterialsAsset);
            if (materials == null)
            {
                materials = ScriptableObject.CreateInstance<LayoutMaterials>();
                AssetDatabase.CreateAsset(materials, LayoutMaterialsAsset);
            }

            materials.Floor = Palette.Floor;
            materials.Wall = Palette.Wall;
            materials.Pillar = Palette.Pillar;
            materials.Aisle = Palette.Aisle;
            materials.LaneLine = Palette.LaneLine;
            materials.ZoneLine = Palette.ZoneLine;
            materials.Handoff = Palette.Handoff;
            materials.Pallet = Palette.Pallet;
            materials.Cardboard = Palette.Cardboard;
            materials.Steel = Palette.Steel;
            materials.MachineBody = Palette.MachineBody;
            EditorUtility.SetDirty(materials);
            AssetDatabase.SaveAssets();
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
