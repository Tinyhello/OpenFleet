using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace OpenFleet.Unity.Editor
{
    /// <summary>
    /// 場景用的材質。存成 Assets/OpenFleet/Materials 下的 .mat，Player build 才會包含（執行期用 Shader.Find 建立的材質可能被裁掉）。
    /// 每次重建場景都會用這裡的數值覆寫材質，調整顏色只要改這個檔案再重建。
    /// </summary>
    internal static class Palette
    {
        public const string Folder = "Assets/OpenFleet/Materials";

        public static Material Floor => Get("Floor", Rgb(0.30f, 0.31f, 0.33f), 0.15f);
        public static Material Aisle => Get("Aisle", Rgb(0.38f, 0.40f, 0.43f), 0.2f);
        public static Material LaneLine => Get("LaneLine", Rgb(0.95f, 0.76f, 0.10f), 0.3f);
        public static Material ZoneLine => Get("ZoneLine", Rgb(0.85f, 0.87f, 0.90f), 0.3f);
        public static Material Handoff => Get("HandoffMarker", Rgb(0.10f, 0.75f, 0.90f), 0.3f, emission: Rgb(0.05f, 0.35f, 0.45f));
        public static Material Wall => Get("Wall", Rgb(0.72f, 0.74f, 0.77f), 0.1f);
        public static Material Pillar => Get("Pillar", Rgb(0.55f, 0.57f, 0.60f), 0.2f);

        public static Material MachineBody => Get("MachineBody", Rgb(0.86f, 0.87f, 0.89f), 0.45f);
        public static Material MachineDark => Get("MachineDark", Rgb(0.16f, 0.17f, 0.19f), 0.4f);
        public static Material Glass => Get("Glass", Rgb(0.18f, 0.26f, 0.32f), 0.9f);
        public static Material Screen => Get("Screen", Rgb(0.05f, 0.12f, 0.20f), 0.8f, emission: Rgb(0.10f, 0.45f, 0.80f));
        public static Material Steel => Get("Steel", Rgb(0.62f, 0.64f, 0.67f), 0.7f, metallic: 0.85f);
        public static Material RoughAccent => Get("AccentRough", Rgb(0.12f, 0.55f, 0.55f), 0.4f);
        public static Material FineAccent => Get("AccentFine", Rgb(0.16f, 0.36f, 0.75f), 0.4f);
        public static Material Furnace => Get("Furnace", Rgb(0.33f, 0.20f, 0.18f), 0.3f);
        public static Material Glow => Get("FurnaceGlow", Rgb(1.00f, 0.45f, 0.10f), 0.5f, emission: Rgb(2.4f, 0.9f, 0.15f));
        public static Material TankWall => Get("TankWall", Rgb(0.20f, 0.42f, 0.30f), 0.5f);
        public static Material Liquid => Get("Liquid", Rgb(0.20f, 0.65f, 0.70f), 0.95f, emission: Rgb(0.02f, 0.12f, 0.14f));
        public static Material QuenchOil => Get("QuenchOil", Rgb(0.10f, 0.08f, 0.05f), 0.95f);
        public static Material Granite => Get("Granite", Rgb(0.12f, 0.12f, 0.13f), 0.6f);
        public static Material PolishWheel => Get("PolishWheel", Rgb(0.80f, 0.70f, 0.45f), 0.2f);

        public static Material RackUpright => Get("RackUpright", Rgb(0.12f, 0.30f, 0.70f), 0.35f);
        public static Material RackBeam => Get("RackBeam", Rgb(0.95f, 0.45f, 0.08f), 0.35f);
        public static Material Cardboard => Get("Cardboard", Rgb(0.72f, 0.54f, 0.32f), 0.1f);
        public static Material Pallet => Get("Pallet", Rgb(0.62f, 0.47f, 0.30f), 0.1f);
        public static Material Bin => Get("Bin", Rgb(0.15f, 0.38f, 0.78f), 0.35f);
        public static Material ToolCabinet => Get("ToolCabinet", Rgb(0.78f, 0.16f, 0.14f), 0.4f);
        public static Material Conveyor => Get("Conveyor", Rgb(0.20f, 0.21f, 0.23f), 0.3f);
        public static Material ChargerPad => Get("ChargerPad", Rgb(0.18f, 0.40f, 0.65f), 0.3f);
        public static Material ChargerLight => Get("ChargerLight", Rgb(0.20f, 0.70f, 1.00f), 0.5f, emission: Rgb(0.30f, 1.20f, 2.00f));

        public static Material AgvChassis => Get("AgvChassis", Rgb(0.18f, 0.19f, 0.21f), 0.45f);
        public static Material AgvLargeDeck => Get("AgvLargeDeck", Rgb(0.96f, 0.58f, 0.10f), 0.45f);
        public static Material AgvSmallBody => Get("AgvSmallBody", Rgb(0.90f, 0.91f, 0.93f), 0.5f);
        public static Material AgvSmallAccent => Get("AgvSmallAccent", Rgb(0.10f, 0.45f, 0.85f), 0.45f);
        public static Material AgvUnknown => Get("AgvUnknown", Rgb(0.55f, 0.35f, 0.65f), 0.4f);
        public static Material Rubber => Get("Rubber", Rgb(0.05f, 0.05f, 0.05f), 0.2f);
        public static Material Sensor => Get("Sensor", Rgb(0.03f, 0.03f, 0.04f), 0.9f);

        /// <summary>顏色由 VehicleView 依連線狀態以 MaterialPropertyBlock 覆寫，這裡只是預設值。</summary>
        public static Material Status => Get("AgvStatus", Rgb(0.45f, 0.45f, 0.45f), 0.5f, emission: Color.black);

        /// <summary>建立或更新所有材質。FBX 依名稱對應材質，所以要在匯入模型前確保 .mat 都存在。</summary>
        public static void EnsureAll()
        {
            foreach (var property in typeof(Palette).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(Material)))
            {
                property.GetValue(null);
            }
        }

        /// <summary>依名稱找 Palette 材質（Blender 的 ".001" 之類尾碼會忽略）。找不到時回傳 null。</summary>
        public static Material Find(string name)
        {
            var dot = name.IndexOf('.');
            var key = (dot > 0 ? name.Substring(0, dot) : name).Replace(" (Instance)", string.Empty).Trim();
            return AssetDatabase.LoadAssetAtPath<Material>($"{Folder}/{key}.mat");
        }

        /// <summary>把模型內嵌的材質換成同名的 Palette 材質；已經是 Palette 材質或找不到對應時保留原本的。</summary>
        public static Material[] Remap(Material[] materials, string context)
        {
            var result = new Material[materials.Length];
            for (var i = 0; i < materials.Length; i++)
            {
                var source = materials[i];
                var mapped = source == null ? null : Find(source.name);
                if (source != null && mapped == null)
                {
                    Debug.LogWarning($"[OpenFleet] {context}: material '{source.name}' has no match in {Folder}.");
                }

                result[i] = mapped != null ? mapped : source;
            }

            return result;
        }

        private static Material Get(string name, Color color, float smoothness, float metallic = 0f, Color? emission = null)
        {
            var path = $"{Folder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", metallic);
            if (emission.HasValue)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission.Value);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else
            {
                material.DisableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", Color.black);
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        private static Color Rgb(float r, float g, float b) => new Color(r, g, b);
    }
}
