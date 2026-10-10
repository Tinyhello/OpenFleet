using UnityEditor;
using UnityEngine;

namespace OpenFleet.Unity.Editor
{
    /// <summary>
    /// Assets/OpenFleet/Models 下的 FBX（由 Blender/scripts 產生）的匯入設定。
    /// Blender 端的材質名稱等於 Palette 的 .mat 檔名，匯入後依名稱換成專案材質，顏色仍由 Palette.cs 統一管理。
    /// </summary>
    internal sealed class ModelImport : AssetPostprocessor
    {
        public const string Folder = "Assets/OpenFleet/Models";

        private bool IsOurs => assetPath.StartsWith(Folder + "/");

        private void OnPreprocessModel()
        {
            if (!IsOurs)
            {
                return;
            }

            var importer = (ModelImporter)assetImporter;
            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importVisibility = false;
            importer.importBlendShapes = false;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.addCollider = false;
            importer.isReadable = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.None;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
        }

        private void OnPostprocessModel(GameObject root)
        {
            if (!IsOurs)
            {
                return;
            }

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                renderer.sharedMaterials = Palette.Remap(renderer.sharedMaterials, assetPath);
            }
        }
    }
}
