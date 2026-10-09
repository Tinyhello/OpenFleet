using UnityEngine;

namespace OpenFleet.Unity.Editor
{
    /// <summary>用基本幾何（Cube／Cylinder）組模型的小工具。座標都是相對父物件的區域座標，單位公尺。</summary>
    internal static class ModelKit
    {
        private static Font _font;

        public static Transform Group(Transform parent, string name, Vector3 position, float yaw = 0f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            return go.transform;
        }

        /// <summary><paramref name="center"/> 是方塊中心，<paramref name="size"/> 是完整尺寸。</summary>
        public static Renderer Box(Transform parent, string name, Vector3 center, Vector3 size, Material material, Vector3? euler = null)
        {
            return Primitive(PrimitiveType.Cube, parent, name, center, size, material, euler);
        }

        /// <summary>預設圓柱軸向朝上（Y）；要橫放就傳 <paramref name="euler"/>，例如 (0,0,90) 讓軸向變成 X。</summary>
        public static Renderer Cylinder(Transform parent, string name, Vector3 center, float diameter, float height, Material material, Vector3? euler = null)
        {
            // Unity 內建圓柱在 scale 1 時高度為 2。
            return Primitive(PrimitiveType.Cylinder, parent, name, center, new Vector3(diameter, height / 2f, diameter), material, euler);
        }

        /// <summary>地面上的矩形框線（例如區域標線、交接點）。</summary>
        public static void FloorFrame(Transform parent, string name, Vector3 center, Vector2 size, float lineWidth, Material material)
        {
            var frame = Group(parent, name, center);
            const float h = 0.02f;
            var y = h / 2f;
            Box(frame, "N", new Vector3(0f, y, size.y / 2f), new Vector3(size.x, h, lineWidth), material);
            Box(frame, "S", new Vector3(0f, y, -size.y / 2f), new Vector3(size.x, h, lineWidth), material);
            Box(frame, "E", new Vector3(size.x / 2f, y, 0f), new Vector3(lineWidth, h, size.y), material);
            Box(frame, "W", new Vector3(-size.x / 2f, y, 0f), new Vector3(lineWidth, h, size.y), material);
        }

        /// <summary>永遠面向攝影機的文字。使用內建字型，缺少的字（例如中文）由作業系統字型補上。</summary>
        public static TextMesh Label(Transform parent, string text, Vector3 position, float characterSize, Color color)
        {
            if (_font == null)
            {
                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;

            var mesh = go.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.font = _font;
            // 字型解析度提高、字元縮小，畫面上大小不變但比較清楚。
            mesh.fontSize = 128;
            mesh.characterSize = characterSize / 2f;
            mesh.anchor = TextAnchor.LowerCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.color = color;
            go.GetComponent<MeshRenderer>().sharedMaterial = _font.material;
            go.AddComponent<Billboard>();
            return mesh;
        }

        private static Renderer Primitive(PrimitiveType type, Transform parent, string name, Vector3 center, Vector3 scale, Material material, Vector3? euler)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localRotation = Quaternion.Euler(euler ?? Vector3.zero);
            go.transform.localScale = scale;

            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            return renderer;
        }
    }
}
