using System;
using System.Collections.Generic;
using OpenFleet.Contracts;
using OpenFleet.Layout;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using static OpenFleet.Unity.Shapes;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 依佈局文件在執行時建出廠房：根物件 <c>Factory</c> 底下有環境（地面、牆、柱子、大門）、路網（鋪面與中心線）與每個模組。
    /// 模組是 Resources/Prefabs/Modules 的 Prefab（由選單 OpenFleet/Build Factory Scene 產生），放在 (X, 0, Z)、依朝向轉 yaw，
    /// 物件名稱就是模組代碼，所以工站的物件名稱＝後端的站點代碼（StationAnchor 依名稱對應）。
    /// 每次 <see cref="Build"/> 都整個重建；佈局版本不常變，不做局部更新。
    /// 位置、尺寸與第 1b 步 Editor 產生的場景相同；暫定的只有路網鋪面的畫法（見 <see cref="BuildRoads"/>）。
    /// </summary>
    public sealed class LayoutScene
    {
        public const string RootName = "Factory";

        // 牆與大門的尺寸沿用第 1b 步的場景。大門寬度是 Claude 挑的（規劃：西牆中間留 10 m 寬的大門）。
        private const float WallHeight = 1.2f, WallThickness = 0.2f, ApronDepth = 5f, GateWidth = 10f;

        // 路網鋪面（寬 1.6 m）、中心黃線（寬 0.06 m）、節點補角的圓（直徑 1.6 m）：規劃時決定的數字。
        private const float RoadWidth = 1.6f, RoadLineWidth = 0.06f;
        private const float RoadHeight = 0.01f, RoadLineHeight = 0.004f, RoadLineY = 0.012f;

        /// <summary>編輯模式的節點小圓點直徑（公尺）。</summary>
        private const float NodeDotDiameter = 0.35f;

        /// <summary>節點小圓點的離地高度（中心）；LayoutEditor 的標記疊在它下面或周圍。</summary>
        public const float NodeDotY = 0.03f;

        private readonly LayoutMaterials _materials;
        private readonly Func<ModuleKind, GameObject> _prefabs;
        private readonly List<StationAnchor> _anchors = new List<StationAnchor>();
        private readonly Dictionary<string, Transform> _modules = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly Dictionary<string, Transform> _nodeDots = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private GameObject _root;

        /// <param name="materials">環境與路網用的材質。</param>
        /// <param name="prefabs">依模組種類取得 Prefab 的方法（由 PresentationController 提供，找不到時回傳 null）。</param>
        public LayoutScene(LayoutMaterials materials, Func<ModuleKind, GameObject> prefabs)
        {
            _materials = materials;
            _prefabs = prefabs;
        }

        /// <summary>目前場景的根物件（還沒建過時是 null）。</summary>
        public Transform Root => _root != null ? _root.transform : null;

        /// <summary>目前場景裡的工站與倉儲站（物件名稱＝站點代碼）。</summary>
        public IReadOnlyList<StationAnchor> Anchors => _anchors;

        /// <summary>模組代碼對應的物件（第 4 步的選取與拖曳用）。</summary>
        public IReadOnlyDictionary<string, Transform> Modules => _modules;

        /// <summary>節點代碼對應的小圓點（只有編輯模式才有；第 5 步拖曳路口時直接移動圓點，不重畫整個場景）。</summary>
        public IReadOnlyDictionary<string, Transform> NodeDots => _nodeDots;

        /// <summary>地面範圍（X 對應 Rect 的 x、Z 對應 y）。</summary>
        public Rect Floor { get; private set; }

        /// <summary>目前場景是不是以編輯模式建的。</summary>
        public bool EditMode { get; private set; }

        /// <summary>移除目前的場景。物件在這個 frame 結束時才真的刪除，所以先停用，畫面上立刻不見。</summary>
        public void Clear()
        {
            _anchors.Clear();
            _modules.Clear();
            _nodeDots.Clear();
            if (_root != null)
            {
                _root.SetActive(false);
                Object.Destroy(_root);
                _root = null;
            }
        }

        /// <summary>
        /// 依 <paramref name="document"/> 重建整個場景。<paramref name="editMode"/> 為 true 時多畫路網節點的小圓點，
        /// 並顯示 AGV 車位的標籤（編輯時車輛隱藏，要靠標籤認出車位）。
        /// </summary>
        public void Build(FactoryLayoutDocument document, bool editMode)
        {
            Clear();
            EditMode = editMode;
            _root = new GameObject(RootName);
            if (document == null || document.Floor == null)
            {
                return;
            }

            var f = document.Floor;
            Floor = Rect.MinMaxRect((float)f.MinX, (float)f.MinZ, (float)f.MaxX, (float)f.MaxZ);

            var root = _root.transform;
            BuildEnvironment(Group(root, "Environment", Vector3.zero), Floor);
            BuildRoads(Group(root, "Roads", Vector3.zero), document, editMode);
            BuildModules(root, document, editMode);
        }

        // ---- 環境 ----

        private void BuildEnvironment(Transform env, Rect floor)
        {
            Box(env, "Floor", new Vector3(floor.center.x, -0.05f, floor.center.y), new Vector3(floor.width, 0.1f, floor.height), _materials.Floor);

            // 牆與柱子。西牆中間是工廠大門，進料與出貨共用。
            var wallFrom = floor.xMin - WallThickness / 2f;
            var wallTo = floor.xMax + WallThickness / 2f;
            Wall(env, "Wall N", true, floor.yMax, wallFrom, wallTo);
            Wall(env, "Wall S", true, floor.yMin, wallFrom, wallTo);
            Wall(env, "Wall E", false, floor.xMax, floor.yMin, floor.yMax);
            BuildMainGate(WallWithDoor(env, "Wall W", false, floor.xMin, floor.yMin, floor.yMax, floor.center.y, GateWidth));

            // 柱子沿南北牆每 8 m 一根，從西端往東 2 m 起算，排在站與站之間，不擋在站點前面。
            for (var x = floor.xMin + 2f; x <= floor.xMax + 0.01f; x += 8f)
            {
                foreach (var z in new[] { floor.yMin, floor.yMax })
                {
                    Box(env, "Pillar", new Vector3(x, 2f, z), new Vector3(0.5f, 4f, 0.5f), _materials.Pillar);
                }
            }
        }

        /// <summary>
        /// 一面有出入口的牆：在 <paramref name="doorAt"/> 留一個寬 <paramref name="doorWidth"/> 的缺口，牆外鋪一塊進出用的地面。
        /// 回傳出入口的群組（+Z 朝廠外），讓呼叫端加上門柱或貨物。其他參數的意義同 <see cref="Wall"/>。
        /// </summary>
        private Transform WallWithDoor(Transform env, string name, bool alongX, float offset, float from, float to, float doorAt, float doorWidth)
        {
            Wall(env, name + " A", alongX, offset, from, doorAt - doorWidth / 2f);
            Wall(env, name + " B", alongX, offset, doorAt + doorWidth / 2f, to);

            var position = alongX ? new Vector3(doorAt, 0f, offset) : new Vector3(offset, 0f, doorAt);
            var outward = alongX ? (offset > 0f ? 0f : 180f) : (offset > 0f ? 90f : -90f);
            var door = Group(env, name + " Door", position, outward);
            Box(door, "Apron", new Vector3(0f, -0.05f, ApronDepth / 2f), new Vector3(doorWidth, 0.1f, ApronDepth), _materials.Aisle);
            Box(door, "Threshold", new Vector3(0f, 0.01f, 0f), new Vector3(doorWidth, 0.02f, 0.3f), _materials.LaneLine);
            return door;
        }

        /// <summary>
        /// 一段牆。<paramref name="alongX"/> 為 true 時沿 X 方向、位於 Z = <paramref name="offset"/>；否則沿 Z 方向、位於 X = offset。
        /// <paramref name="from"/>／<paramref name="to"/> 是牆在延伸方向上的起訖。
        /// </summary>
        private void Wall(Transform env, string name, bool alongX, float offset, float from, float to)
        {
            var mid = (from + to) / 2f;
            var length = to - from;
            Box(env, name,
                alongX ? new Vector3(mid, WallHeight / 2f, offset) : new Vector3(offset, WallHeight / 2f, mid),
                alongX ? new Vector3(length, WallHeight, WallThickness) : new Vector3(WallThickness, WallHeight, length),
                _materials.Wall);
        }

        /// <summary>
        /// 工廠大門（西牆中間，進料與出貨共用）：門柱、門楣、標示，以及兩側的貨物。
        /// 群組的 +Z 朝廠外、+X 朝北：門外南側是剛到廠還沒入庫的原料，
        /// 門內北側（靠成品倉那一邊）是包裝好等著出貨的成品箱。貨物只是示意，沒有對應的後端資料。
        /// </summary>
        private void BuildMainGate(Transform gate)
        {
            var half = GateWidth / 2f;
            foreach (var x in new[] { -half, half })
            {
                Box(gate, "Post", new Vector3(x, 2.25f, 0f), new Vector3(0.5f, 4.5f, 0.5f), _materials.Pillar);
            }

            Box(gate, "Header", new Vector3(0f, 4.65f, 0f), new Vector3(GateWidth + 0.5f, 0.4f, 0.5f), _materials.LaneLine);
            Label(gate, "大門\nMain Gate", new Vector3(0f, 5f, 0f), 0.09f, Color.white);

            // 進料：門外南側。
            foreach (var x in new[] { -3.9f, -2.6f })
            {
                Box(gate, "Inbound Pallet", new Vector3(x, 0.07f, 2.4f), new Vector3(1.1f, 0.14f, 1.1f), _materials.Pallet);
                Box(gate, "Inbound Crate", new Vector3(x, 0.44f, 2.4f), new Vector3(1.0f, 0.6f, 1.0f), _materials.Cardboard);
            }

            Label(gate, "進料\nInbound", new Vector3(-3.25f, 1f, 3.6f), 0.07f, Color.white);

            // 出貨：門內北側，沿著西牆排到出貨站前面。
            for (var i = 0; i < 3; i++)
            {
                var x = half + 1.4f + i * 1.35f;
                Box(gate, "Outbound Pallet", new Vector3(x, 0.07f, -1.3f), new Vector3(1.1f, 0.14f, 1.1f), _materials.Pallet);
                for (var level = 0; level < 3 - i % 2; level++)
                {
                    Box(gate, "Outbound Box", new Vector3(x, 0.36f + level * 0.46f, -1.3f), new Vector3(1.0f, 0.44f, 1.0f), _materials.Cardboard);
                }
            }

            Label(gate, "待出貨\nOutbound", new Vector3(half + 2.75f, 1.9f, -1.3f), 0.07f, Color.white);
        }

        // ---- 路網 ----

        /// <summary>
        /// 每條路段一片鋪面（寬 1.6 m）加一條中心黃線；每個節點一個圓形鋪面補轉角，交接點上的節點不畫圓（模組本身有交接點方框）。
        /// 沒有畫車道邊線與分隔線：車道不限方向，由後端的路段預約決定誰先走。
        /// 這是第 3 步的暫定畫法，取代第 1b 步場景裡內外兩條車道的整片鋪面。
        /// 編輯模式另外畫節點的小圓點：交接點節點用交接點的顏色，其他是白色（用 MaterialPropertyBlock 上色，不複製材質）。
        /// 路段與節點的選取、錯誤標記是編輯器另外疊上去的（LayoutEditor），這裡只畫底。
        /// </summary>
        private void BuildRoads(Transform roads, FactoryLayoutDocument document, bool editMode)
        {
            var nodes = new Dictionary<string, LayoutRoadNode>(StringComparer.Ordinal);
            foreach (var node in document.RoadNodes ?? new List<LayoutRoadNode>())
            {
                if (node != null && node.Code != null && !nodes.ContainsKey(node.Code))
                {
                    nodes.Add(node.Code, node);
                }
            }

            foreach (var edge in document.RoadEdges ?? new List<LayoutRoadEdge>())
            {
                if (edge == null || edge.From == null || edge.To == null
                    || !nodes.TryGetValue(edge.From, out var a) || !nodes.TryGetValue(edge.To, out var b))
                {
                    continue;
                }

                var dx = (float)(b.X - a.X);
                var dz = (float)(b.Z - a.Z);
                var length = Mathf.Sqrt(dx * dx + dz * dz);
                if (length < 0.001f)
                {
                    continue;
                }

                // 方塊的長邊是區域 Z 軸；yaw 從 +Z 往 +X 轉（和 Unity 相同）。
                var yaw = new Vector3(0f, Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 0f);
                var mid = new Vector3((float)(a.X + b.X) / 2f, 0f, (float)(a.Z + b.Z) / 2f);
                Flat(Box(roads, "Road " + edge.From + "~" + edge.To, mid + new Vector3(0f, RoadHeight / 2f, 0f),
                    new Vector3(RoadWidth, RoadHeight, length), _materials.Aisle, yaw));
                Flat(Box(roads, "Road Line", mid + new Vector3(0f, RoadLineY, 0f),
                    new Vector3(RoadLineWidth, RoadLineHeight, length), _materials.LaneLine, yaw));
            }

            var handoffs = new List<(double X, double Z)>();
            foreach (var module in document.Modules ?? new List<LayoutModule>())
            {
                if (module != null && Enum.IsDefined(typeof(ModuleKind), module.Kind) && LayoutGeometry.HandoffPoint(module) is { } point)
                {
                    handoffs.Add(point);
                }
            }

            MaterialPropertyBlock dotBlock = null;
            if (editMode)
            {
                dotBlock = new MaterialPropertyBlock();
                dotBlock.SetColor(Shader.PropertyToID("_BaseColor"), PresentationController.Colors.Node);
                dotBlock.SetColor(Shader.PropertyToID("_EmissionColor"), PresentationController.Colors.Node);
            }

            foreach (var node in nodes.Values)
            {
                var atHandoff = false;
                foreach (var point in handoffs)
                {
                    if (LayoutGeometry.SamePoint((node.X, node.Z), point, LayoutRules.HandoffMatch))
                    {
                        atHandoff = true;
                        break;
                    }
                }

                var center = new Vector3((float)node.X, 0f, (float)node.Z);
                if (!atHandoff)
                {
                    Flat(Cylinder(roads, "Node " + node.Code, center + new Vector3(0f, RoadHeight / 2f, 0f), RoadWidth, RoadHeight, _materials.Aisle));
                }

                if (editMode)
                {
                    var dot = Cylinder(roads, "Node Dot " + node.Code, center + new Vector3(0f, NodeDotY, 0f), NodeDotDiameter, 0.02f,
                        atHandoff ? _materials.Handoff : _materials.ZoneLine);
                    Flat(dot);
                    if (!atHandoff)
                    {
                        dot.SetPropertyBlock(dotBlock);
                    }

                    _nodeDots[node.Code] = dot.transform;
                }
            }
        }

        /// <summary>貼地的薄片不投影：數量多（一條路段兩片），投影沒有視覺效果。</summary>
        private static void Flat(Renderer renderer)
        {
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        // ---- 模組 ----

        private void BuildModules(Transform root, FactoryLayoutDocument document, bool editMode)
        {
            // 站點放在 Factory/Stations，車位與裝飾物各一組；物件名稱都是模組代碼。
            var stations = Group(root, "Stations", Vector3.zero);
            var bays = Group(root, "Bays", Vector3.zero);
            var decor = Group(root, "Decor", Vector3.zero);

            foreach (var module in document.Modules ?? new List<LayoutModule>())
            {
                if (module == null || string.IsNullOrEmpty(module.Code) || !Enum.IsDefined(typeof(ModuleKind), module.Kind))
                {
                    continue;
                }

                var prefab = _prefabs(module.Kind);
                if (prefab == null)
                {
                    continue;
                }

                var parent = ModuleCatalog.IsStation(module.Kind) ? stations : ModuleCatalog.IsBay(module.Kind) ? bays : decor;
                var go = Object.Instantiate(prefab, parent);
                go.name = module.Code;
                go.transform.localPosition = new Vector3((float)module.X, 0f, (float)module.Z);
                go.transform.localRotation = Quaternion.Euler(0f, module.Rotation, 0f);

                var label = go.transform.Find(VehicleView.LabelName);
                if (label != null)
                {
                    var text = label.GetComponent<TextMesh>();
                    if (text != null)
                    {
                        text.text = ModuleNaming.DisplayName(document, module) + "\n" + ModuleNaming.EnglishName(document, module);
                    }

                    // 車位平常不寫字（車上有自己的標籤），編輯時車輛隱藏，才靠車位的標籤認出是哪一個。
                    if (ModuleCatalog.IsBay(module.Kind))
                    {
                        label.gameObject.SetActive(editMode);
                    }
                }

                if (ModuleCatalog.IsStation(module.Kind))
                {
                    _anchors.Add(go.AddComponent<StationAnchor>());
                }

                if (!_modules.ContainsKey(module.Code))
                {
                    _modules.Add(module.Code, go.transform);
                }
            }
        }
    }
}
