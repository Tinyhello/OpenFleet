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

        // 牆與大門的尺寸沿用第 1b 步的場景。大門寬度 10 m 是 Claude 挑的，和驗證用同一個常數。
        private const float WallHeight = 1.2f, WallThickness = 0.2f, ApronDepth = 5f, GateWidth = (float)LayoutRules.GateWidth;

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
        private readonly List<Mesh> _meshes = new List<Mesh>();
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

        /// <summary>廠房外框（外牆頂點的範圍；X 對應 Rect 的 x、Z 對應 y）。形狀本身在文件的 Outline。</summary>
        public Rect Floor { get; private set; }

        /// <summary>目前場景是不是以編輯模式建的。</summary>
        public bool EditMode { get; private set; }

        /// <summary>移除目前的場景。物件在這個 frame 結束時才真的刪除，所以先停用，畫面上立刻不見。</summary>
        public void Clear()
        {
            _anchors.Clear();
            _modules.Clear();
            _nodeDots.Clear();
            foreach (var mesh in _meshes)
            {
                if (mesh != null)
                {
                    Object.Destroy(mesh);
                }
            }

            _meshes.Clear();
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

            var bounds = LayoutFloorShape.Bounds(document.Floor);
            Floor = Rect.MinMaxRect((float)bounds.MinX, (float)bounds.MinZ, (float)bounds.MaxX, (float)bounds.MaxZ);

            var root = _root.transform;
            BuildEnvironment(Group(root, "Environment", Vector3.zero), document.Floor);
            BuildRoads(Group(root, "Roads", Vector3.zero), document, editMode);
            BuildModules(root, document, editMode);
        }

        // ---- 環境 ----

        /// <summary>
        /// 依外牆多邊形建環境（使用者 2026-10-25 決定：廠房是任意多邊形、牆可以斜、大門在任一面牆上）：
        /// 地板是三角化的網格，每段牆一個沿牆方向旋轉的方塊，柱子沿每段牆排列，大門那段牆留缺口。
        /// 形狀一律透過 <see cref="LayoutFloorShape"/> 取（舊文件只有矩形，也能畫），不直接讀 Min／Max。
        /// 預設佈局（矩形、大門在西牆中間）畫出來和多邊形化之前相同，只有東西牆多了幾根柱子。
        /// </summary>
        private void BuildEnvironment(Transform env, LayoutFloor floor)
        {
            var outline = LayoutFloorShape.Outline(floor);
            if (outline.Count < 3)
            {
                return;
            }

            BuildFloorMesh(env, outline);

            var gate = LayoutFloorShape.Gate(floor);
            // 大門不在任何牆上時（驗證會報 gate-invalid）仍畫在最近的那段牆上，編輯時才看得到它在哪裡。
            var gateHit = FloorGeometry.NearestWall(outline, gate.X, gate.Z);
            var gateWall = gateHit != null ? gateHit.Index : -1;

            for (var i = 0; i < outline.Count; i++)
            {
                var start = FloorGeometry.WallStart(outline, i);
                var end = FloorGeometry.WallEnd(outline, i);
                if (i != gateWall)
                {
                    WallSegment(env, "Wall " + i, start, end, true, true);
                    continue;
                }

                // 大門那段牆：門中心兩側各留一半門寬的缺口，缺口那一端不用補轉角。
                var length = (float)FloorGeometry.WallLength(outline, i);
                var along = Mathf.Clamp((float)gateHit.Along, 0f, length);
                var openFrom = along - GateWidth / 2f;
                var openTo = along + GateWidth / 2f;
                if (openFrom > 0f)
                {
                    WallSegment(env, "Wall " + i + " A", start, FloorGeometry.PointOnWall(outline, i, openFrom / length), true, false);
                }

                if (openTo < length)
                {
                    WallSegment(env, "Wall " + i + " B", FloorGeometry.PointOnWall(outline, i, openTo / length), end, false, true);
                }

                var normal = FloorGeometry.WallOutwardNormal(outline, i);
                var yaw = Mathf.Atan2((float)normal.X, (float)normal.Z) * Mathf.Rad2Deg;
                var door = Group(env, "Gate Door", new Vector3((float)gateHit.X, 0f, (float)gateHit.Z), yaw);
                Box(door, "Apron", new Vector3(0f, -0.05f, ApronDepth / 2f), new Vector3(GateWidth, 0.1f, ApronDepth), _materials.Aisle);
                Box(door, "Threshold", new Vector3(0f, 0.01f, 0f), new Vector3(GateWidth, 0.02f, 0.3f), _materials.LaneLine);
                BuildMainGate(door);
            }

            // 柱子：每段牆從起點（外牆的順序）往後 2 m 起每 8 m 一根，排在站與站之間，不擋在站點前面。
            // 預設佈局南北牆的位置和以前相同；大門的缺口（含門柱）裡不放。
            for (var i = 0; i < outline.Count; i++)
            {
                var length = (float)FloorGeometry.WallLength(outline, i);
                if (length < 0.01f)
                {
                    continue;
                }

                var a = FloorGeometry.WallStart(outline, i);
                var b = FloorGeometry.WallEnd(outline, i);
                var yaw = Mathf.Atan2((float)(b.X - a.X), (float)(b.Z - a.Z)) * Mathf.Rad2Deg;
                for (var d = 2f; d <= length + 0.01f; d += 8f)
                {
                    if (i == gateWall && Mathf.Abs(d - (float)gateHit.Along) < GateWidth / 2f + 0.5f)
                    {
                        continue;
                    }

                    var p = FloorGeometry.PointOnWall(outline, i, Mathf.Min(1f, d / length));
                    Box(env, "Pillar", new Vector3((float)p.X, 2f, (float)p.Z), new Vector3(0.5f, 4f, 0.5f), _materials.Pillar, new Vector3(0f, yaw, 0f));
                }
            }
        }

        /// <summary>
        /// 地板：外牆多邊形三角化後的網格，上表面在 y = 0（和原本的地板方塊同高）。
        /// 不做側面與底面：側面被牆擋住，攝影機也看不到底面。網格不屬於任何資產，重建時要自己銷毀（見 <see cref="Clear"/>）。
        /// 三角形的繞行方向不靠文件的約定，逐個用叉積判斷、讓法線朝上（Unity 的正面是順時針）。
        /// </summary>
        private void BuildFloorMesh(Transform env, List<LayoutPoint> outline)
        {
            var triangles = FloorGeometry.Triangulate(outline);
            if (triangles.Length < 3)
            {
                return;
            }

            var vertices = new Vector3[outline.Count];
            var normals = new Vector3[outline.Count];
            var uv = new Vector2[outline.Count];
            for (var i = 0; i < outline.Count; i++)
            {
                vertices[i] = new Vector3((float)outline[i].X, 0f, (float)outline[i].Z);
                normals[i] = Vector3.up;
                uv[i] = new Vector2((float)outline[i].X, (float)outline[i].Z);
            }

            var indices = new int[triangles.Length];
            for (var k = 0; k + 2 < triangles.Length; k += 3)
            {
                var a = triangles[k];
                var b = triangles[k + 1];
                var c = triangles[k + 2];
                if (Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).y < 0f)
                {
                    (b, c) = (c, b);
                }

                indices[k] = a;
                indices[k + 1] = b;
                indices[k + 2] = c;
            }

            var mesh = new Mesh { name = "Floor (runtime)", vertices = vertices, normals = normals, uv = uv, triangles = indices };
            mesh.RecalculateBounds();
            _meshes.Add(mesh);

            var go = new GameObject("Floor");
            go.transform.SetParent(env, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _materials.Floor;
        }

        /// <summary>
        /// 一段牆（從 a 到 b）：方塊沿牆方向旋轉，高 1.2 m、厚 0.2 m。
        /// <paramref name="extendA"/>／<paramref name="extendB"/> 為 true 時那一端多伸出半個牆厚，把轉角補滿
        /// （直角轉角完全補滿；斜的轉角外側會留一個小缺口，是已知的外觀限制）。
        /// </summary>
        private void WallSegment(Transform env, string name, (double X, double Z) a, (double X, double Z) b, bool extendA, bool extendB)
        {
            var dx = (float)(b.X - a.X);
            var dz = (float)(b.Z - a.Z);
            var length = Mathf.Sqrt(dx * dx + dz * dz);
            if (length < 0.01f)
            {
                return;
            }

            var from = extendA ? -WallThickness / 2f : 0f;
            var to = length + (extendB ? WallThickness / 2f : 0f);
            var mid = (from + to) / 2f;
            var center = new Vector3((float)a.X + dx / length * mid, WallHeight / 2f, (float)a.Z + dz / length * mid);
            // 方塊的長邊是區域 Z 軸；yaw 從 +Z 往 +X 轉。
            Box(env, name, center, new Vector3(WallThickness, WallHeight, to - from), _materials.Wall,
                new Vector3(0f, Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 0f));
        }

        /// <summary>
        /// 工廠大門（預設在西牆中間，可放在任一面牆；進料與出貨共用）：門柱、門楣、標示，以及兩側的貨物。
        /// 群組的 +Z 朝廠外（依那段牆的朝外法線旋轉）、+X 朝預設佈局的北：門外南側是剛到廠還沒入庫的原料，
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

            // 出貨：門內北側，沿著牆排到出貨站前面。
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
