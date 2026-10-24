using UnityEditor;
using UnityEngine;

namespace OpenFleet.Unity.Editor
{
    /// <summary>
    /// 載入 Blender 模型的小工具（需要 Editor API，所以留在 Editor 組件）。
    /// 基本幾何與文字標籤（Group、Box、Cylinder、FloorFrame、Label）在執行期組件的 <see cref="Shapes"/>，執行時建環境也用同一份。
    /// </summary>
    internal static class ModelKit
    {
        /// <summary>
        /// 放置 Blender 產生的模型（Assets/OpenFleet/Models/&lt;name&gt;.fbx，原始檔在 Blender/Models/&lt;name&gt;.blend）。
        /// 模型原點、尺寸與原本用基本幾何組裝的版本相同。<paramref name="swaps"/> 用來替換材質，例如粗／精加工的強調色。
        /// </summary>
        public static GameObject Model(Transform parent, string name, Vector3 position, float yaw = 0f, params (Material from, Material to)[] swaps)
        {
            var path = $"{ModelImport.Folder}/{name}.fbx";
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                Debug.LogError($"[OpenFleet] Model not found: {path}. Export it from Blender/scripts/{name}.py.");
                return null;
            }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
            go.name = name;
            go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one;

            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                // 匯入時通常已換成 Palette 材質；材質是後來才建立的情況在這裡補上。
                var materials = Palette.Remap(renderer.sharedMaterials, path);
                for (var i = 0; i < materials.Length; i++)
                {
                    foreach (var (from, to) in swaps)
                    {
                        if (materials[i] == from)
                        {
                            materials[i] = to;
                        }
                    }
                }

                renderer.sharedMaterials = materials;
            }

            return go;
        }
    }
}
