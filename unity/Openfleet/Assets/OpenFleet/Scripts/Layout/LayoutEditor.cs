using System;
using System.Collections.Generic;
using OpenFleet.Contracts;
using OpenFleet.Layout;
using UnityEngine;
using UnityEngine.InputSystem;
using static OpenFleet.Unity.Shapes;
using Colors = OpenFleet.Unity.PresentationController.Colors;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 沙盒佈局編輯器（P7 第 4 步）：草稿、選取、擺放與拖曳、滑鼠與鍵盤、驗證結果。
    /// 掛在 OpenFleet 物件上（FleetView 的 RequireComponent 會自動加上，不必在場景裡設定）；面板（ProductionPanel.Layout.cs）只呼叫這裡的方法、聽 <see cref="Changed"/>。
    /// 所有修改都經過共用程式庫的 <see cref="LayoutEditSession"/>（新增、移動、旋轉、刪除、廠房大小、復原、驗證），這裡只做輸入與繪製。
    ///
    /// 進入編輯：草稿是佈局的複本；<see cref="LayoutController.EditMode"/> 設成 true（編輯中收到的新版本離開時才套用）、
    /// 車輛與進度條隱藏（PresentationController.SetLayoutEditing）、站點先換成空的（編輯場景的站點不交給 PresentationController，
    /// 進度條也就不會綁到草稿的物件上）、場景改畫草稿、地面顯示 1 m 格線。工廠在後端照常運轉。
    /// 離開編輯：場景回到使用中的佈局，車輛與進度條恢復。
    ///
    /// 滑鼠從攝影機射到 y = 0 的地面，用模組的占地矩形（LayoutGeometry.Footprint）判斷點到誰，不靠物理碰撞體。
    /// 左鍵按在模組上＝選取並開始拖曳（位置對齊 0.2 m）；按在空地＝取消選取；右鍵與中鍵拖曳仍是攝影機。
    /// 拖曳或擺放中只移動物件，占地框線依 <see cref="LayoutEditSession.CanPlace"/> 即時變綠或紅；
    /// 放開（放下）時才改草稿、整個場景重畫、跑驗證。重疊或超出廠房不擋，放得下去，只是會在檢查結果裡報錯（規劃的決定）。
    ///
    /// 路線編輯（P7 第 5 步）：兩個工具，<see cref="RoadTool.Select"/> 與 <see cref="RoadTool.Draw"/>。
    /// 選取工具點擊的順序是節點（0.5 m）→ 路段（0.4 m）→ 模組；節點可拖曳（交接點的節點不行），路段與節點可刪除。
    /// 畫路線工具：按下是起點、拖到終點放開，起訖點吸附與 Shift 鎖軸都用 <see cref="RoadPicking"/>。
    /// 路網的選取與錯誤標記是疊在場景上的一組薄片（每次重畫或選取改變才重建，數量只有被標記的那幾個），不是每個 frame 重畫。
    /// </summary>
    public sealed class LayoutEditor : MonoBehaviour
    {
        /// <summary>格線間距（公尺）。使用者要的是 1 m 格線。</summary>
        private const float GridSpacing = 1f;

        /// <summary>格線寬度、離地高度。Claude 暫定：夠細、不蓋過路網（路面頂端 0.01 m）。</summary>
        private const float GridLineWidth = 0.03f, GridLineHeight = 0.006f;

        /// <summary>拖曳要先離開按下位置幾個像素才算拖曳，免得單純點選時模組跟著抖動一格。Claude 暫定。</summary>
        private const float DragStartPixels = 4f;

        /// <summary>地面墊片（選取、錯誤、擺放中時鋪在模組占地下）的名稱與厚度；頂面 0.013 m，高於路面（0.01 m）。</summary>
        private const string PadName = "Edit Pad";
        private const float PadY = 0.0115f, PadThickness = 0.003f;

        /// <summary>墊片顏色是框線顏色的幾倍亮度（墊片面積大，壓暗一點才不會蓋過模型）。Claude 暫定。</summary>
        private const float PadBrightness = 0.7f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private PresentationController _presentation;
        private LayoutController _controller;
        private LayoutEditSession _session;
        private LayoutValidationDto _validation = new LayoutValidationDto();
        private MaterialPropertyBlock _block;
        private GameObject _overlay;
        private Transform _gridRoot;
        private Material _gridMaterial;
        private Rect _gridFloor;
        private Transform _ghost;
        private bool? _ghostOk;
        private double _ghostX, _ghostZ;
        private string _selected;
        private ModuleKind? _placing;
        private int _placingRotation;
        private string _dragging;
        private bool _dragArmed;
        private Vector2 _dragPressScreen;
        private double _dragGrabX, _dragGrabZ, _dragStartX, _dragStartZ, _dragX, _dragZ;

        // 路線編輯
        private RoadTool _tool;
        private string _selectedNode;
        private string _selectedEdgeFrom, _selectedEdgeTo;
        private LayoutIssueMarks _marks = new LayoutIssueMarks();
        private Transform _marksRoot;
        private string _dragNode;
        private bool _dragNodeArmed;
        private Vector2 _dragNodePress;
        private double _nodeStartX, _nodeStartZ, _nodeTargetX, _nodeTargetZ;
        private bool _drawing;
        private RoadSnap _drawStart;
        private double _drawEndX, _drawEndZ;
        private Transform _previewRoot;
        private readonly List<Transform> _previewLines = new List<Transform>();
        private Transform _drawLine;
        private Transform _snapMarker;
        private Renderer[] _snapRenderers;
        private TextMesh _lengthLabel;

        /// <summary>草稿有變、選取或擺放狀態有變時發出，面板據此更新（同一個 frame 內可能發出多次）。</summary>
        public event Action Changed;

        /// <summary>是否在編輯模式。</summary>
        public bool Editing => _session != null;

        /// <summary>草稿（唯讀，不要直接改；復原與重做會換掉物件，每次使用都重新讀）。不在編輯中時是 null。</summary>
        public FactoryLayoutDocument Document => _session?.Document;

        /// <summary>草稿來自哪一份佈局（儲存與套用用）；不在編輯中時是 null。</summary>
        public Guid? SourceId { get; private set; }

        public string SourceName { get; private set; } = string.Empty;

        public bool SourceIsDefault { get; private set; }

        public bool IsDirty => _session != null && _session.IsDirty;

        public bool CanUndo => _session != null && _session.CanUndo;

        public bool CanRedo => _session != null && _session.CanRedo;

        /// <summary>最近一次的檢查結果。</summary>
        public LayoutValidationDto Validation => _validation;

        public int ErrorCount { get; private set; }

        public int WarningCount { get; private set; }

        /// <summary>選取的模組代碼；沒有選取是 null。</summary>
        public string Selected => _selected;

        /// <summary>目前的工具。</summary>
        public RoadTool Tool => _tool;

        /// <summary>選取的是什麼（一次只能選一種）。</summary>
        public SelectionKind SelectionType =>
            _selected != null ? SelectionKind.Module : _selectedNode != null ? SelectionKind.Node : _selectedEdgeFrom != null ? SelectionKind.Edge : SelectionKind.None;

        public LayoutRoadNode SelectedNode => FindNode(_selectedNode);

        public LayoutRoadEdge SelectedEdge => FindEdge(_selectedEdgeFrom, _selectedEdgeTo);

        /// <summary>選取的節點是不是模組的交接點（交接點跟著模組走，不能拖、不能刪）。</summary>
        public bool SelectedNodeIsHandoff => _session != null && _selectedNode != null && _session.IsHandoffNode(_selectedNode);

        /// <summary>節點連著幾條路段。</summary>
        public int EdgeCountOf(string nodeCode)
        {
            var count = 0;
            if (_session != null)
            {
                foreach (var edge in _session.Document.RoadEdges)
                {
                    if (edge.From == nodeCode || edge.To == nodeCode)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        public LayoutModule SelectedModule => FindModule(_selected);

        /// <summary>正在擺放的種類；沒有在擺放是 null。</summary>
        public ModuleKind? Placing => _placing;

        public bool CanDeleteSelected =>
            _session != null
            && (_selected != null ? _session.CanRemove(_selected)
                : _selectedNode != null ? FindNode(_selectedNode) != null && !_session.IsHandoffNode(_selectedNode)
                : _selectedEdgeFrom != null && FindEdge(_selectedEdgeFrom, _selectedEdgeTo) != null);

        public int CountOf(ModuleKind kind) => _session?.CountOf(kind) ?? 0;

        public bool CanAdd(ModuleKind kind) => _session != null && _session.CanAdd(kind);

        private void Awake()
        {
            _presentation = GetComponent<PresentationController>();
            _controller = GetComponent<LayoutController>();
        }

        private void OnDestroy()
        {
            DestroyOverlay();
        }

        // ---- 進入與離開 ----

        /// <summary>
        /// 以 <paramref name="layout"/> 的複本當草稿開始編輯（已經在編輯時換成另一份草稿）。
        /// 佈局還沒建好、材質沒載入時回傳 false。
        /// </summary>
        public bool Enter(LayoutDto layout)
        {
            if (layout?.Document == null || _controller.Scene == null || _presentation.LayoutMaterials == null)
            {
                return false;
            }

            _session = new LayoutEditSession(layout.Document);
            SourceId = layout.Id;
            SourceName = layout.Name;
            SourceIsDefault = layout.IsDefault;
            ResetInteraction();

            _controller.EditMode = true;
            _presentation.SetLayoutEditing(true);
            // 草稿的站點不交給 PresentationController：先換成空的，進度條整批清掉，之後輪詢到的狀態也不會再建立。
            _presentation.SetStations(Array.Empty<StationAnchor>());
            EnsureOverlay();
            Rebuild(true);
            return true;
        }

        /// <summary>
        /// 離開編輯。<paramref name="restoreActive"/> 為 true 時場景立刻回到使用中的佈局；
        /// 套用成功後傳 false，由 <see cref="LayoutController.Reload"/> 載入新的使用中佈局（那之前先留著草稿的畫面）。
        /// 草稿有沒有存由呼叫端（面板）決定，這裡不問。
        /// </summary>
        public void Exit(bool restoreActive = true)
        {
            if (!Editing)
            {
                return;
            }

            ResetInteraction();
            _session = null;
            DestroyOverlay();

            if (restoreActive)
            {
                _controller.LeaveEditMode();
            }
            else
            {
                _controller.EditMode = false;
            }

            _presentation.SetLayoutEditing(false);
            Changed?.Invoke();
        }

        /// <summary>草稿已經存到後端：記下它現在對應哪一份佈局，並把「已儲存」的基準更新（復原回到這個狀態就不再是未儲存）。</summary>
        public void MarkSaved(Guid id, string name, bool isDefault)
        {
            if (!Editing)
            {
                return;
            }

            SourceId = id;
            SourceName = name;
            SourceIsDefault = isDefault;
            _session.MarkSaved();
            Changed?.Invoke();
        }

        // ---- 編輯操作（面板與鍵盤共用） ----

        /// <summary>選取模組；傳 null 取消所有選取（模組、節點、路段）。</summary>
        public void Select(string code)
        {
            SetSelection(code, null, null, null);
        }

        public void SelectNode(string code)
        {
            SetSelection(null, code, null, null);
        }

        public void SelectEdge(string from, string to)
        {
            SetSelection(null, null, from, to);
        }

        private void SetSelection(string module, string node, string edgeFrom, string edgeTo)
        {
            if (!Editing)
            {
                return;
            }

            // 不存在的東西不能選（取消選取＝全部 null）。
            module = FindModule(module) != null ? module : null;
            node = FindNode(node) != null ? node : null;
            if (FindEdge(edgeFrom, edgeTo) == null)
            {
                edgeFrom = edgeTo = null;
            }

            if (module == _selected && node == _selectedNode && edgeFrom == _selectedEdgeFrom && edgeTo == _selectedEdgeTo)
            {
                return;
            }

            var previous = _selected;
            _selected = module;
            _selectedNode = node;
            _selectedEdgeFrom = edgeFrom;
            _selectedEdgeTo = edgeTo;
            Restyle(previous);
            Restyle(_selected);
            RestyleRoads();
            Changed?.Invoke();
        }

        /// <summary>
        /// 換工具。進行中的拖曳、畫線、擺放都取消（規劃：切換工具就取消）。
        /// 選取不變：換到畫路線工具後還看得到剛才選的東西，Delete 也仍然有效。
        /// </summary>
        public void SetTool(RoadTool tool)
        {
            if (!Editing || tool == _tool)
            {
                return;
            }

            CancelInteractions();
            _tool = tool;
            HidePreview();
            Changed?.Invoke();
        }

        /// <summary>取消拖曳（模組、節點）、畫線、擺放，不改工具與選取。</summary>
        private void CancelInteractions()
        {
            if (_dragging != null)
            {
                CancelDrag();
            }

            if (_dragNode != null)
            {
                CancelNodeDrag();
            }

            if (_drawing)
            {
                CancelDraw();
            }

            if (_placing != null)
            {
                EndPlacing();
            }
        }

        /// <summary>進入、離開編輯時把互動狀態全部歸零（不通知、不重畫）。</summary>
        private void ResetInteraction()
        {
            EndPlacing();
            _dragging = null;
            _dragNode = null;
            _drawing = false;
            _tool = RoadTool.Select;
            _selected = null;
            _selectedNode = null;
            _selectedEdgeFrom = _selectedEdgeTo = null;
            _marksRoot = null;
            _marks = new LayoutIssueMarks();
            DestroyPreview();
        }

        public void StartPlacing(ModuleKind kind)
        {
            if (!Editing || !_session.CanAdd(kind))
            {
                return;
            }

            // 擺放模組用選取工具的操作（左鍵放下），在畫路線工具下開始擺放就切回選取工具。
            CancelInteractions();
            _tool = RoadTool.Select;
            HidePreview();
            Select(null);
            _placing = kind;
            _placingRotation = 0;
            var prefab = _presentation.ModulePrefab(kind);
            if (prefab != null)
            {
                _ghost = Instantiate(prefab, _overlay.transform).transform;
                _ghost.name = "Ghost";
                var label = _ghost.Find(VehicleView.LabelName);
                if (label != null && label.TryGetComponent<TextMesh>(out var text))
                {
                    text.text = ModuleCatalog.Get(kind).ZhName;
                }

                // 滑鼠移進場景之前不顯示。
                _ghost.gameObject.SetActive(false);
            }

            Changed?.Invoke();
        }

        public void CancelPlacing()
        {
            if (_placing == null)
            {
                return;
            }

            EndPlacing();
            Changed?.Invoke();
        }

        public void RotateSelected()
        {
            if (_placing != null)
            {
                _placingRotation = (_placingRotation + 90) % 360;
                Changed?.Invoke();
                return;
            }

            if (_selected != null && _session.RotateModule(_selected))
            {
                Rebuild(false);
            }
        }

        public void DeleteSelected()
        {
            if (!CanDeleteSelected)
            {
                return;
            }

            if (_selected != null)
            {
                if (_session.RemoveModule(_selected))
                {
                    _selected = null;
                    Rebuild(false);
                }
            }
            else if (_selectedNode != null)
            {
                if (_session.DeleteNode(_selectedNode))
                {
                    _selectedNode = null;
                    Rebuild(false);
                }
            }
            else if (_session.DeleteEdge(_selectedEdgeFrom, _selectedEdgeTo))
            {
                _selectedEdgeFrom = _selectedEdgeTo = null;
                Rebuild(false);
            }
        }

        public void Undo()
        {
            if (Editing && !Interacting && _session.Undo())
            {
                AfterHistory();
            }
        }

        public void Redo()
        {
            if (Editing && !Interacting && _session.Redo())
            {
                AfterHistory();
            }
        }

        /// <summary>拖曳或畫線進行中（這時復原、重做、刪除、旋轉都不處理）。</summary>
        private bool Interacting => _dragging != null || _dragNode != null || _drawing;

        private void AfterHistory()
        {
            // 復原與重做會換掉整份文件：選的東西還在就保留，不在了就取消。
            if (FindModule(_selected) == null)
            {
                _selected = null;
            }

            if (FindNode(_selectedNode) == null)
            {
                _selectedNode = null;
            }

            if (FindEdge(_selectedEdgeFrom, _selectedEdgeTo) == null)
            {
                _selectedEdgeFrom = _selectedEdgeTo = null;
            }

            Rebuild(true);
        }

        /// <summary>廠房大小（公尺）；對齊 0.4 m、限制在規則範圍內。拖滑桿時連續呼叫，復原只算一步。</summary>
        public void SetFloorSize(double width, double depth)
        {
            if (Editing && _session.SetFloorSize(width, depth))
            {
                Rebuild(true);
            }
        }

        /// <summary>
        /// 點檢查結果的一條：選取它的第一個模組（沒有模組就看第一個節點、再沒有就看第一條路段）並把攝影機移過去。
        /// 節點與路段也一併選取（P7 第 5 步），讓使用者看得到是哪一個、可以直接刪除。
        /// </summary>
        public void FocusIssue(LayoutIssueDto issue)
        {
            if (!Editing || issue == null)
            {
                return;
            }

            Vector3? point = null;
            if (issue.ModuleCodes != null && issue.ModuleCodes.Count > 0 && FindModule(issue.ModuleCodes[0]) is { } module)
            {
                Select(module.Code);
                var rect = LayoutGeometry.Footprint(module);
                point = new Vector3((float)(rect.MinX + rect.MaxX) / 2f, 0f, (float)(rect.MinZ + rect.MaxZ) / 2f);
            }
            else if (issue.NodeCodes != null && issue.NodeCodes.Count > 0 && FindNode(issue.NodeCodes[0]) is { } node)
            {
                SelectNode(node.Code);
                point = new Vector3((float)node.X, 0f, (float)node.Z);
            }
            else if (issue.Edges != null && issue.Edges.Count > 0 && FindEdge(issue.Edges[0].From, issue.Edges[0].To) is { } edge
                && FindNode(edge.From) is { } a && FindNode(edge.To) is { } b)
            {
                SelectEdge(edge.From, edge.To);
                point = new Vector3((float)(a.X + b.X) / 2f, 0f, (float)(a.Z + b.Z) / 2f);
            }

            if (point.HasValue && _presentation.CameraController != null)
            {
                _presentation.CameraController.Focus(point.Value);
            }
        }

        // ---- 滑鼠與鍵盤 ----

        private void Update()
        {
            if (!Editing)
            {
                return;
            }

            HandleKeyboard(Keyboard.current);
            HandleMouse(Mouse.current);
        }

        private void HandleKeyboard(Keyboard keyboard)
        {
            // 輸入框有焦點時鍵盤是用來打字的。
            if (keyboard == null || ProductionPanel.TextInputFocused)
            {
                return;
            }

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                if (_dragging != null)
                {
                    CancelDrag();
                }
                else if (_dragNode != null)
                {
                    CancelNodeDrag();
                }
                else if (_drawing)
                {
                    // 只取消這一筆，工具不變，可以接著畫下一筆。
                    CancelDraw();
                }
                else if (_placing != null)
                {
                    CancelPlacing();
                }
                else
                {
                    Select(null);
                }

                return;
            }

            if (keyboard.ctrlKey.isPressed)
            {
                if (keyboard.zKey.wasPressedThisFrame)
                {
                    // Ctrl+Shift+Z 也當作重做。
                    if (keyboard.shiftKey.isPressed)
                    {
                        Redo();
                    }
                    else
                    {
                        Undo();
                    }
                }
                else if (keyboard.yKey.wasPressedThisFrame)
                {
                    Redo();
                }

                return;
            }

            if (Interacting)
            {
                return;
            }

            if (keyboard.rKey.wasPressedThisFrame)
            {
                RotateSelected();
            }
            else if (keyboard.deleteKey.wasPressedThisFrame)
            {
                DeleteSelected();
            }
        }

        private void HandleMouse(Mouse mouse)
        {
            if (mouse == null)
            {
                return;
            }

            var screen = mouse.position.ReadValue();
            var overUi = ProductionPanel.PointerOverPanel || FleetView.OverlayRect.Contains(new Vector2(screen.x, Screen.height - screen.y));
            var hasPoint = TryGroundPoint(screen, out var x, out var z);

            if (_dragging != null)
            {
                if (mouse.leftButton.isPressed)
                {
                    if (hasPoint)
                    {
                        UpdateDrag(screen, x, z);
                    }
                }
                else
                {
                    EndDrag();
                }

                return;
            }

            if (_dragNode != null)
            {
                if (mouse.leftButton.isPressed)
                {
                    if (hasPoint)
                    {
                        UpdateNodeDrag(screen, x, z);
                    }
                }
                else
                {
                    EndNodeDrag();
                }

                return;
            }

            if (_drawing)
            {
                if (mouse.leftButton.isPressed)
                {
                    if (hasPoint)
                    {
                        UpdateDraw(x, z);
                    }
                }
                else
                {
                    EndDraw();
                }

                return;
            }

            if (_placing != null)
            {
                UpdateGhost(!overUi && hasPoint, x, z);
                if (!overUi && hasPoint && mouse.leftButton.wasPressedThisFrame)
                {
                    PlaceGhost();
                }

                return;
            }

            if (_tool == RoadTool.Draw)
            {
                // 畫路線工具：左鍵只用來畫線，不選取也不拖曳模組；沒按時顯示吸附目標。
                ShowSnapHover(!overUi && hasPoint, x, z);
                if (!overUi && hasPoint && mouse.leftButton.wasPressedThisFrame)
                {
                    ProductionPanel.ClearFocus();
                    BeginDraw(x, z);
                }

                return;
            }

            if (!overUi && hasPoint && mouse.leftButton.wasPressedThisFrame)
            {
                // 點場景時讓面板的輸入框放開焦點，鍵盤快捷鍵才會恢復。
                ProductionPanel.ClearFocus();
                PressSelect(screen, x, z);
            }
        }

        /// <summary>選取工具按下左鍵：節點（0.5 m 內）→ 路段（0.4 m 內）→ 模組。</summary>
        private void PressSelect(Vector2 screen, double x, double z)
        {
            var document = _session.Document;
            var nodeCode = RoadPicking.NearestNode(document, x, z, LayoutRules.PickNodeRadius);
            if (nodeCode != null)
            {
                SelectNode(nodeCode);
                // 交接點的節點跟著模組走，不能拖（面板會提示）。
                if (!_session.IsHandoffNode(nodeCode))
                {
                    BeginNodeDrag(nodeCode, screen);
                }

                return;
            }

            var edge = RoadPicking.NearestEdge(document, x, z, LayoutRules.PickEdgeRadius);
            if (edge != null)
            {
                SelectEdge(edge.From, edge.To);
                return;
            }

            var module = HitModule(x, z);
            Select(module?.Code);
            if (module != null)
            {
                BeginDrag(module, screen, x, z);
            }
        }

        /// <summary>螢幕座標射到 y = 0 的地面（朝上看不到地面時回傳 false）。</summary>
        private static bool TryGroundPoint(Vector2 screen, out double x, out double z)
        {
            x = z = 0;
            var camera = Camera.main;
            if (camera == null)
            {
                return false;
            }

            var ray = camera.ScreenPointToRay(screen);
            if (!new Plane(Vector3.up, 0f).Raycast(ray, out var distance))
            {
                return false;
            }

            var point = ray.GetPoint(distance);
            x = point.x;
            z = point.z;
            return true;
        }

        /// <summary>點在哪個模組的占地裡；重疊時取面積最小的（小的蓋在大的上面，例如車位在載具存放區旁）。</summary>
        private LayoutModule HitModule(double x, double z)
        {
            LayoutModule best = null;
            var bestArea = double.MaxValue;
            foreach (var module in _session.Document.Modules)
            {
                var rect = LayoutGeometry.Footprint(module);
                if (x < rect.MinX || x > rect.MaxX || z < rect.MinZ || z > rect.MaxZ)
                {
                    continue;
                }

                var area = (rect.MaxX - rect.MinX) * (rect.MaxZ - rect.MinZ);
                if (area <= bestArea)
                {
                    best = module;
                    bestArea = area;
                }
            }

            return best;
        }

        // ---- 拖曳 ----

        private void BeginDrag(LayoutModule module, Vector2 screen, double x, double z)
        {
            _dragging = module.Code;
            _dragArmed = false;
            _dragPressScreen = screen;
            // 記下抓取點和模組原點的差，拖起來才不會一按就跳到滑鼠位置。
            _dragGrabX = module.X - x;
            _dragGrabZ = module.Z - z;
            _dragStartX = _dragX = module.X;
            _dragStartZ = _dragZ = module.Z;
        }

        private void UpdateDrag(Vector2 screen, double x, double z)
        {
            if (!_dragArmed)
            {
                if ((screen - _dragPressScreen).sqrMagnitude < DragStartPixels * DragStartPixels)
                {
                    return;
                }

                _dragArmed = true;
            }

            var module = FindModule(_dragging);
            if (module == null || !_controller.Scene.Modules.TryGetValue(_dragging, out var transform) || transform == null)
            {
                _dragging = null;
                return;
            }

            _dragX = LayoutEditSession.Snap(x + _dragGrabX, LayoutEditSession.ModuleGrid);
            _dragZ = LayoutEditSession.Snap(z + _dragGrabZ, LayoutEditSession.ModuleGrid);
            transform.localPosition = new Vector3((float)_dragX, 0f, (float)_dragZ);
            ShowPlacement(transform, module.Kind, _session.CanPlace(module.Kind, _dragX, _dragZ, module.Rotation, module.Code));
        }

        private void EndDrag()
        {
            var code = _dragging;
            _dragging = null;
            var moved = _dragArmed && (_dragX != _dragStartX || _dragZ != _dragStartZ);
            _dragArmed = false;
            if (moved && _session.MoveModule(code, _dragX, _dragZ))
            {
                Rebuild(false);
            }
            else
            {
                // 沒有真的移動（或拖回原位）：恢復原本的框線樣式。
                CancelDragVisual(code);
            }
        }

        private void CancelDrag()
        {
            var code = _dragging;
            _dragging = null;
            _dragArmed = false;
            CancelDragVisual(code);
        }

        private void CancelDragVisual(string code)
        {
            var module = FindModule(code);
            if (module != null && _controller.Scene.Modules.TryGetValue(code, out var transform) && transform != null)
            {
                transform.localPosition = new Vector3((float)module.X, 0f, (float)module.Z);
            }

            Restyle(code);
        }

        // ---- 路線：拖曳節點 ----

        private void BeginNodeDrag(string code, Vector2 screen)
        {
            var node = FindNode(code);
            if (node == null)
            {
                return;
            }

            _dragNode = code;
            _dragNodeArmed = false;
            _dragNodePress = screen;
            _nodeStartX = _nodeTargetX = node.X;
            _nodeStartZ = _nodeTargetZ = node.Z;
        }

        /// <summary>
        /// 拖曳中只動預覽：節點的小圓點跟著目標移動、連著的路段畫細的預覽線，鋪面不重畫（放開才重畫）。
        /// 目標位置（Claude 暫定）：別的節點在 <see cref="LayoutRules.SnapNodeRadius"/> 內就吸到那個節點（放開時 MoveNode 會合併），
        /// 否則對齊 0.2 m 格點；不吸附到路段上（拖節點的目的是改路口位置，吸到路段中間不會接上去）。
        /// </summary>
        private void UpdateNodeDrag(Vector2 screen, double x, double z)
        {
            if (!_dragNodeArmed)
            {
                if ((screen - _dragNodePress).sqrMagnitude < DragStartPixels * DragStartPixels)
                {
                    return;
                }

                _dragNodeArmed = true;
                // 標記是依原位置畫的，拖曳中先收起來（目標位置改由預覽顯示）。
                if (_marksRoot != null)
                {
                    _marksRoot.gameObject.SetActive(false);
                }
            }

            var node = FindNode(_dragNode);
            if (node == null || !_controller.Scene.NodeDots.TryGetValue(_dragNode, out var dot) || dot == null)
            {
                CancelNodeDrag();
                return;
            }

            var target = NearestOtherNode(_dragNode, x, z, LayoutRules.SnapNodeRadius);
            var snapped = target != null;
            if (snapped)
            {
                _nodeTargetX = target.X;
                _nodeTargetZ = target.Z;
            }
            else
            {
                _nodeTargetX = LayoutEditSession.Snap(x, LayoutRules.RoadGrid);
                _nodeTargetZ = LayoutEditSession.Snap(z, LayoutRules.RoadGrid);
            }

            dot.localPosition = new Vector3((float)_nodeTargetX, LayoutScene.NodeDotY, (float)_nodeTargetZ);
            PaintDot(dot, Colors.RoadSelected);

            EnsurePreview();
            var used = 0;
            foreach (var edge in _session.Document.RoadEdges)
            {
                var otherCode = edge.From == _dragNode ? edge.To : edge.To == _dragNode ? edge.From : null;
                if (otherCode == null || FindNode(otherCode) is not { } other)
                {
                    continue;
                }

                var line = PreviewLine(used++);
                PlaceLine(line, _nodeTargetX, _nodeTargetZ, other.X, other.Z, PreviewWidth, PreviewY);
                Paint(line.GetComponent<Renderer>(), Colors.RoadSelected);
            }

            for (var i = used; i < _previewLines.Count; i++)
            {
                Show(_previewLines[i], false);
            }

            ShowSnapMarker(_nodeTargetX, _nodeTargetZ, snapped ? RoadSnapKind.Node : RoadSnapKind.Grid);
        }

        private LayoutRoadNode NearestOtherNode(string exclude, double x, double z, double radius)
        {
            LayoutRoadNode best = null;
            var bestDistance = radius * radius + 1e-9;
            foreach (var node in _session.Document.RoadNodes)
            {
                if (node.Code == exclude)
                {
                    continue;
                }

                var d = (node.X - x) * (node.X - x) + (node.Z - z) * (node.Z - z);
                if (d <= bestDistance)
                {
                    best = node;
                    bestDistance = d;
                }
            }

            return best;
        }

        private void EndNodeDrag()
        {
            var code = _dragNode;
            var moved = _dragNodeArmed && (_nodeTargetX != _nodeStartX || _nodeTargetZ != _nodeStartZ);
            if (moved)
            {
                // 合併時 MoveNode 回傳被合併到的那個節點，選取跟著它。
                var result = _session.MoveNode(code, _nodeTargetX, _nodeTargetZ);
                if (result != null)
                {
                    _dragNode = null;
                    _dragNodeArmed = false;
                    HidePreview();
                    _selectedNode = result;
                    Rebuild(false);
                    return;
                }
            }

            CancelNodeDrag();
        }

        private void CancelNodeDrag()
        {
            var code = _dragNode;
            _dragNode = null;
            _dragNodeArmed = false;
            HidePreview();
            var node = FindNode(code);
            if (node != null && _controller.Scene.NodeDots.TryGetValue(code, out var dot) && dot != null)
            {
                dot.localPosition = new Vector3((float)node.X, LayoutScene.NodeDotY, (float)node.Z);
                PaintDot(dot, Colors.Node);
            }

            RestyleRoads();
        }

        private void PaintDot(Transform dot, Color color)
        {
            if (dot.TryGetComponent<Renderer>(out var renderer))
            {
                Paint(renderer, color);
            }
        }

        // ---- 路線：畫路線 ----

        private void BeginDraw(double x, double z)
        {
            _drawStart = RoadPicking.Snap(_session.Document, x, z);
            _drawing = true;
            UpdateDraw(x, z);
        }

        /// <summary>終點用 <see cref="RoadPicking.Snap"/>；按住 Shift 時先鎖水平或垂直（<see cref="RoadPicking.SnapLocked"/>）。</summary>
        private void UpdateDraw(double x, double z)
        {
            var keyboard = Keyboard.current;
            var locked = keyboard != null && keyboard.shiftKey.isPressed;
            var end = locked
                ? RoadPicking.SnapLocked(_session.Document, _drawStart.X, _drawStart.Z, x, z)
                : RoadPicking.Snap(_session.Document, x, z);
            _drawEndX = end.X;
            _drawEndZ = end.Z;

            var length = DrawLength;
            var ok = length >= LayoutRules.MinDrawLength;
            var color = ok ? Colors.DrawOk : Colors.DrawTooShort;
            EnsurePreview();
            Show(_drawLine, true);
            PlaceLine(_drawLine, _drawStart.X, _drawStart.Z, _drawEndX, _drawEndZ, PreviewWidth, PreviewY);
            Paint(_drawLine.GetComponent<Renderer>(), color);

            // 長度標籤在終點上方；太短時改紅色（放開也不會畫）。
            // 預設視角（約 56 m）下世界座標的字太小，所以依攝影機距離放大，畫面上的大小固定（暫定，見 LabelPixels）。
            Show(_lengthLabel.transform, true);
            var labelPosition = new Vector3((float)_drawEndX, 0.5f, (float)_drawEndZ);
            _lengthLabel.transform.localPosition = labelPosition;
            _lengthLabel.transform.localScale = Vector3.one * (LabelPixels * WorldPerPixel(labelPosition) / LabelEmMeters);
            var text = $"{length:0.0} m";
            if (_lengthLabel.text != text)
            {
                _lengthLabel.text = text;
            }

            _lengthLabel.color = ok ? Color.white : Colors.DrawTooShort;
            ShowSnapMarker(_drawEndX, _drawEndZ, end.Kind);
        }

        private double DrawLength => Math.Sqrt((_drawEndX - _drawStart.X) * (_drawEndX - _drawStart.X) + (_drawEndZ - _drawStart.Z) * (_drawEndZ - _drawStart.Z));

        private void EndDraw()
        {
            var length = DrawLength;
            _drawing = false;
            HidePreview();
            if (length >= LayoutRules.MinDrawLength && _session.AddRoad(_drawStart.X, _drawStart.Z, _drawEndX, _drawEndZ) != null)
            {
                // 畫完不選取任何東西（新路段可能有好幾段），整個場景重畫並驗證。
                _selected = null;
                _selectedNode = null;
                _selectedEdgeFrom = _selectedEdgeTo = null;
                Rebuild(false);
            }
        }

        private void CancelDraw()
        {
            _drawing = false;
            HidePreview();
        }

        /// <summary>畫路線工具沒按下時：滑鼠所在位置的吸附目標。</summary>
        private void ShowSnapHover(bool visible, double x, double z)
        {
            if (!visible)
            {
                HidePreview();
                return;
            }

            var snap = RoadPicking.Snap(_session.Document, x, z);
            EnsurePreview();
            ShowSnapMarker(snap.X, snap.Z, snap.Kind);
        }

        // ---- 路線：預覽物件（畫線預覽、吸附標記、長度標籤、拖曳預覽線） ----

        /// <summary>預覽線的寬度、離地高度（中心）、厚度；高於路面與標記。</summary>
        private const float PreviewWidth = 0.14f, PreviewY = 0.045f, PreviewThickness = 0.01f;

        private void EnsurePreview()
        {
            if (_previewRoot != null)
            {
                return;
            }

            var material = _presentation.LayoutMaterials.ZoneLine;
            _previewRoot = Group(_overlay.transform, "Road Preview", Vector3.zero);
            var line = Box(_previewRoot, "Draw Line", Vector3.zero, new Vector3(1f, PreviewThickness, 1f), material);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _drawLine = line.transform;
            _drawLine.gameObject.SetActive(false);

            _snapMarker = Group(_previewRoot, "Snap Marker", Vector3.zero);
            FloorFrame(_snapMarker, "Frame", Vector3.zero, new Vector2(0.6f, 0.6f), 0.06f, material);
            _snapRenderers = _snapMarker.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in _snapRenderers)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                Paint(renderer, Colors.SnapMarker);
            }

            _snapMarker.gameObject.SetActive(false);

            _lengthLabel = Label(_previewRoot, string.Empty, Vector3.zero, 0.07f, Color.white);
            _lengthLabel.gameObject.SetActive(false);
        }

        /// <summary>吸附標記：吸到節點是大框、吸到路段中等、只是格點最小（一眼看得出吸到什麼）。</summary>
        private void ShowSnapMarker(double x, double z, RoadSnapKind kind)
        {
            Show(_snapMarker, true);
            _snapMarker.localPosition = new Vector3((float)x, LayoutScene.NodeDotY, (float)z);
            // 拉遠時標記跟著放大（預設視角 56 m 以內維持原大小），不會縮到看不見；尺寸暫定。
            var far = Mathf.Max(1f, CameraDistance(_snapMarker.position) / DefaultCameraDistance);
            _snapMarker.localScale = Vector3.one * (kind == RoadSnapKind.Node ? 1.1f : kind == RoadSnapKind.Edge ? 0.8f : 0.45f) * far;
        }

        // 預設攝影機距離（公尺）；長度標籤與吸附標記的螢幕大小以此為準。暫定。
        private const float DefaultCameraDistance = 56f;

        // 長度標籤的字高（像素，1280×720 約 15 px，和站名標籤相近）。暫定。
        private const float LabelPixels = 15f;

        // TextMesh 字級 128、characterSize 0.07 / 2 時，一個字的高度約 6.4 × 0.07 公尺（縮放 1）。
        private const float LabelEmMeters = 6.4f * 0.07f;

        private static float CameraDistance(Vector3 worldPosition)
        {
            var camera = Camera.main;
            return camera != null ? Vector3.Distance(camera.transform.position, worldPosition) : DefaultCameraDistance;
        }

        // 在這個位置，螢幕上一個像素等於幾公尺（透視攝影機）。
        private static float WorldPerPixel(Vector3 localPosition)
        {
            var camera = Camera.main;
            var height = Mathf.Max(1, Screen.height);
            if (camera == null || camera.orthographic)
            {
                return 2f * DefaultCameraDistance * Mathf.Tan(30f * Mathf.Deg2Rad) / height;
            }

            return 2f * CameraDistance(localPosition) * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / height;
        }

        private Transform PreviewLine(int index)
        {
            while (_previewLines.Count <= index)
            {
                var line = Box(_previewRoot, "Drag Line", Vector3.zero, new Vector3(1f, PreviewThickness, 1f), _presentation.LayoutMaterials.ZoneLine);
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _previewLines.Add(line.transform);
            }

            var transform = _previewLines[index];
            Show(transform, true);
            return transform;
        }

        private void HidePreview()
        {
            if (_previewRoot == null)
            {
                return;
            }

            Show(_drawLine, false);
            Show(_snapMarker, false);
            Show(_lengthLabel != null ? _lengthLabel.transform : null, false);
            foreach (var line in _previewLines)
            {
                Show(line, false);
            }
        }

        private void DestroyPreview()
        {
            if (_previewRoot != null)
            {
                _previewRoot.gameObject.SetActive(false);
                Destroy(_previewRoot.gameObject);
            }

            _previewRoot = null;
            _drawLine = null;
            _snapMarker = null;
            _snapRenderers = null;
            _lengthLabel = null;
            _previewLines.Clear();
        }

        private static void Show(Transform transform, bool visible)
        {
            if (transform != null && transform.gameObject.activeSelf != visible)
            {
                transform.gameObject.SetActive(visible);
            }
        }

        /// <summary>把單位方塊擺成從 (ax, az) 到 (bx, bz) 的細長條（方塊的 Z 軸是長邊，yaw 從 +Z 往 +X 轉）。</summary>
        private static void PlaceLine(Transform line, double ax, double az, double bx, double bz, float width, float y)
        {
            var dx = (float)(bx - ax);
            var dz = (float)(bz - az);
            var length = Mathf.Max(0.001f, Mathf.Sqrt(dx * dx + dz * dz));
            line.localPosition = new Vector3((float)(ax + bx) / 2f, y, (float)(az + bz) / 2f);
            line.localRotation = Quaternion.Euler(0f, Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 0f);
            line.localScale = new Vector3(width, PreviewThickness, length);
        }

        // ---- 路線：選取與錯誤標記 ----

        /// <summary>
        /// 路段與節點的標記（選取黃、錯誤紅、警告橘）：場景根底下的一組薄片，每次重畫、選取改變時整組重建
        /// （只有被標記的幾個，不是整個路網），不在每個 frame 做事。正常模式沒有這一組。
        /// 選取與錯誤同時存在時：路段是紅色寬條上疊黃色窄條；節點是黃色大圓下面墊紅色小圓（選取還看得到、錯誤也看得到）。
        /// 尺寸與高度都是 Claude 暫定。
        /// </summary>
        private void RestyleRoads()
        {
            if (!Editing || _controller.Scene == null || _controller.Scene.Root == null || _presentation.LayoutMaterials == null)
            {
                return;
            }

            var root = _controller.Scene.Root;
            if (_marksRoot == null || _marksRoot.parent != root)
            {
                _marksRoot = Group(root, "Road Marks", Vector3.zero);
            }
            else
            {
                for (var i = _marksRoot.childCount - 1; i >= 0; i--)
                {
                    var child = _marksRoot.GetChild(i).gameObject;
                    child.SetActive(false);
                    Destroy(child);
                }
            }

            _marksRoot.gameObject.SetActive(true);
            var document = _session.Document;
            var nodes = new Dictionary<string, LayoutRoadNode>(StringComparer.Ordinal);
            foreach (var node in document.RoadNodes)
            {
                nodes[node.Code] = node;
            }

            foreach (var edge in document.RoadEdges)
            {
                var error = _marks.HasErrorEdge(edge.From, edge.To);
                var selected = _selectedEdgeFrom != null
                    && ((edge.From == _selectedEdgeFrom && edge.To == _selectedEdgeTo) || (edge.From == _selectedEdgeTo && edge.To == _selectedEdgeFrom));
                if ((!error && !selected) || !nodes.TryGetValue(edge.From, out var a) || !nodes.TryGetValue(edge.To, out var b))
                {
                    continue;
                }

                if (error)
                {
                    MarkEdge(a, b, Colors.RoadError, 0.5f, 0.017f);
                }

                if (selected)
                {
                    MarkEdge(a, b, Colors.RoadSelected, error ? 0.2f : 0.5f, 0.022f);
                }
            }

            foreach (var node in document.RoadNodes)
            {
                var selected = node.Code == _selectedNode;
                var error = _marks.ErrorNodeCodes.Contains(node.Code);
                var warning = !error && _marks.WarningNodeCodes.Contains(node.Code);
                if (selected)
                {
                    MarkNode(node, Colors.RoadSelected, 0.95f, 0.021f);
                }

                if (error || warning)
                {
                    MarkNode(node, error ? Colors.RoadError : Colors.RoadWarning, 0.65f, 0.026f);
                }
            }
        }

        private void MarkEdge(LayoutRoadNode a, LayoutRoadNode b, Color color, float width, float y)
        {
            var renderer = Box(_marksRoot, "Edge Mark", Vector3.zero, new Vector3(1f, 0.004f, 1f), _presentation.LayoutMaterials.ZoneLine);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            PlaceLine(renderer.transform, a.X, a.Z, b.X, b.Z, width, y);
            renderer.transform.localScale = new Vector3(width, 0.004f, renderer.transform.localScale.z);
            Paint(renderer, color);
        }

        private void MarkNode(LayoutRoadNode node, Color color, float diameter, float y)
        {
            var renderer = Cylinder(_marksRoot, "Node Mark", new Vector3((float)node.X, y, (float)node.Z), diameter, 0.008f,
                _presentation.LayoutMaterials.ZoneLine);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Paint(renderer, color);
        }

        // ---- 擺放 ----

        private void UpdateGhost(bool visible, double mouseX, double mouseZ)
        {
            if (_ghost == null)
            {
                return;
            }

            if (_ghost.gameObject.activeSelf != visible)
            {
                _ghost.gameObject.SetActive(visible);
            }

            if (!visible || _placing == null)
            {
                return;
            }

            // 讓占地的中心跟著滑鼠（模組原點到占地中心有偏移，依朝向旋轉）。
            var kind = _placing.Value;
            var spec = ModuleCatalog.Get(kind);
            var (offsetX, offsetZ) = LayoutGeometry.Rotate(spec.OffsetX, spec.OffsetZ, _placingRotation);
            _ghostX = LayoutEditSession.Snap(mouseX - offsetX, LayoutEditSession.ModuleGrid);
            _ghostZ = LayoutEditSession.Snap(mouseZ - offsetZ, LayoutEditSession.ModuleGrid);
            _ghost.localPosition = new Vector3((float)_ghostX, 0f, (float)_ghostZ);
            _ghost.localRotation = Quaternion.Euler(0f, _placingRotation, 0f);
            ShowPlacement(_ghost, kind, _session.CanPlace(kind, _ghostX, _ghostZ, _placingRotation, null));
        }

        private void PlaceGhost()
        {
            if (_placing == null || _ghost == null || !_ghost.gameObject.activeSelf)
            {
                return;
            }

            var module = _session.AddModule(_placing.Value, _ghostX, _ghostZ, _placingRotation);
            EndPlacing();
            if (module != null)
            {
                _selected = module.Code;
            }

            Rebuild(false);
        }

        private void EndPlacing()
        {
            _placing = null;
            _ghostOk = null;
            if (_ghost != null)
            {
                _ghost.gameObject.SetActive(false);
                Destroy(_ghost.gameObject);
                _ghost = null;
            }
        }

        // ---- 重畫與驗證 ----

        /// <summary>
        /// 草稿有變：整個場景依草稿重畫（約 6 ms）、跑驗證、依結果重新上色，然後通知面板。
        /// <paramref name="redrawGrid"/>：廠房大小可能變了，格線要重畫。
        /// </summary>
        private void Rebuild(bool redrawGrid)
        {
            var document = _session.Document;
            _controller.Scene.Build(document, true);
            if (redrawGrid)
            {
                BuildGrid(_controller.Scene.Floor);
            }

            Validate();
            RestyleAll();
            Changed?.Invoke();
        }

        private void Validate()
        {
            _validation = _session.Validate();
            _marks = LayoutIssueMarks.From(_validation);
            ErrorCount = 0;
            WarningCount = 0;
            foreach (var issue in _validation.Issues)
            {
                if (issue.Severity == LayoutIssueSeverity.Error)
                {
                    ErrorCount++;
                }
                else
                {
                    WarningCount++;
                }
            }
        }

        private LayoutModule FindModule(string code)
        {
            if (_session == null || code == null)
            {
                return null;
            }

            foreach (var module in _session.Document.Modules)
            {
                if (module.Code == code)
                {
                    return module;
                }
            }

            return null;
        }

        private LayoutRoadNode FindNode(string code)
        {
            if (_session == null || code == null)
            {
                return null;
            }

            foreach (var node in _session.Document.RoadNodes)
            {
                if (node.Code == code)
                {
                    return node;
                }
            }

            return null;
        }

        /// <summary>找路段（不分方向）。</summary>
        private LayoutRoadEdge FindEdge(string from, string to)
        {
            if (_session == null || from == null || to == null)
            {
                return null;
            }

            foreach (var edge in _session.Document.RoadEdges)
            {
                if ((edge.From == from && edge.To == to) || (edge.From == to && edge.To == from))
                {
                    return edge;
                }
            }

            return null;
        }

        private bool HasError(string code)
        {
            foreach (var issue in _validation.Issues)
            {
                if (issue.Severity == LayoutIssueSeverity.Error && issue.ModuleCodes != null && issue.ModuleCodes.Contains(code))
                {
                    return true;
                }
            }

            return false;
        }

        private void RestyleAll()
        {
            foreach (var code in _controller.Scene.Modules.Keys)
            {
                Restyle(code);
            }

            RestyleRoads();
        }

        /// <summary>
        /// 依狀態給一個模組的框線與墊片上色：選取是黃色，有錯誤是紅色；兩者同時時框線黃、墊片紅（選取還看得到、錯誤也看得到）。
        /// 都不是時恢復原本的樣子。
        /// </summary>
        private void Restyle(string code)
        {
            if (code == null || !Editing || _controller.Scene == null || !_controller.Scene.Modules.TryGetValue(code, out var transform) || transform == null)
            {
                return;
            }

            var module = FindModule(code);
            if (module == null)
            {
                return;
            }

            var selected = code == _selected;
            var error = HasError(code);
            if (!selected && !error)
            {
                SetFrame(transform, null);
                SetPad(transform, module.Kind, null);
                return;
            }

            SetFrame(transform, selected ? Colors.LayoutSelected : Colors.LayoutError);
            SetPad(transform, module.Kind, error ? Colors.LayoutError : Colors.LayoutSelected);
        }

        /// <summary>拖曳或擺放中：框線與墊片依能不能放變綠或紅。顏色沒變就不重設。</summary>
        private void ShowPlacement(Transform transform, ModuleKind kind, bool ok)
        {
            if (transform == _ghost)
            {
                if (_ghostOk == ok)
                {
                    return;
                }

                _ghostOk = ok;
            }

            var color = ok ? Colors.PlaceOk : Colors.PlaceBlocked;
            SetFrame(transform, color);
            SetPad(transform, kind, color);
        }

        // ---- 繪製：框線、墊片、格線 ----

        /// <summary>
        /// 占地框線（Prefab 的 Zone 群組）用 MaterialPropertyBlock 換顏色，不複製材質；<paramref name="color"/> 為 null 時恢復原色。
        /// 車位的框線材質有 Emission，一併設定，不然顏色被自發光蓋過。
        /// </summary>
        private void SetFrame(Transform module, Color? color)
        {
            var zone = module.Find("Zone");
            if (zone == null)
            {
                return;
            }

            foreach (var renderer in zone.GetComponentsInChildren<Renderer>(true))
            {
                if (color.HasValue)
                {
                    Paint(renderer, color.Value);
                }
                else
                {
                    renderer.SetPropertyBlock(null);
                }
            }
        }

        /// <summary>占地下面鋪一片同色的薄墊片（框線只有幾公分寬，遠看不明顯）；<paramref name="color"/> 為 null 時移除。</summary>
        private void SetPad(Transform module, ModuleKind kind, Color? color)
        {
            var existing = module.Find(PadName);
            if (!color.HasValue)
            {
                if (existing != null)
                {
                    existing.gameObject.SetActive(false);
                    Destroy(existing.gameObject);
                }

                return;
            }

            if (existing == null)
            {
                var spec = ModuleCatalog.Get(kind);
                var renderer = Box(module, PadName, new Vector3((float)spec.OffsetX, PadY, (float)spec.OffsetZ),
                    new Vector3((float)spec.Width, PadThickness, (float)spec.Depth), _presentation.LayoutMaterials.ZoneLine);
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                existing = renderer.transform;
            }

            var c = color.Value;
            Paint(existing.GetComponent<Renderer>(), new Color(c.r * PadBrightness, c.g * PadBrightness, c.b * PadBrightness, 1f));
        }

        private void Paint(Renderer renderer, Color color)
        {
            if (_block == null)
            {
                _block = new MaterialPropertyBlock();
            }

            _block.SetColor(BaseColorId, color);
            _block.SetColor(EmissionColorId, color);
            renderer.SetPropertyBlock(_block);
        }

        private void EnsureOverlay()
        {
            if (_overlay != null)
            {
                return;
            }

            _overlay = new GameObject("Layout Editor");
            _overlay.transform.SetParent(transform, false);
            _gridRoot = Group(_overlay.transform, "Grid", Vector3.zero);
            _gridFloor = default;
        }

        private void DestroyOverlay()
        {
            if (_overlay != null)
            {
                _overlay.SetActive(false);
                Destroy(_overlay);
                _overlay = null;
            }

            _ghost = null;
            _gridRoot = null;
            if (_gridMaterial != null)
            {
                Destroy(_gridMaterial);
                _gridMaterial = null;
            }
        }

        /// <summary>
        /// 地面的 1 m 格線：貼在地面上的細長方塊，顏色用自己的一份材質（複製自 ZoneLine，離開編輯時銷毀）。
        /// 廠房大小沒變就不重畫。
        /// </summary>
        private void BuildGrid(Rect floor)
        {
            if (_gridRoot == null || floor == _gridFloor)
            {
                return;
            }

            _gridFloor = floor;
            for (var i = _gridRoot.childCount - 1; i >= 0; i--)
            {
                var child = _gridRoot.GetChild(i).gameObject;
                child.SetActive(false);
                Destroy(child);
            }

            if (_gridMaterial == null)
            {
                _gridMaterial = new Material(_presentation.LayoutMaterials.ZoneLine) { name = "Edit Grid (runtime)" };
                _gridMaterial.SetColor(BaseColorId, Colors.Grid);
            }

            // 線落在整數座標上（和模組的 0.2 m 格點、1 m 的看圖習慣對得上）。
            var y = GridLineHeight / 2f;
            for (var x = Mathf.Ceil(floor.xMin / GridSpacing) * GridSpacing; x <= floor.xMax + 0.001f; x += GridSpacing)
            {
                GridLine(new Vector3(x, y, floor.center.y), new Vector3(GridLineWidth, GridLineHeight, floor.height));
            }

            for (var z = Mathf.Ceil(floor.yMin / GridSpacing) * GridSpacing; z <= floor.yMax + 0.001f; z += GridSpacing)
            {
                GridLine(new Vector3(floor.center.x, y, z), new Vector3(floor.width, GridLineHeight, GridLineWidth));
            }
        }

        private void GridLine(Vector3 center, Vector3 size)
        {
            var renderer = Box(_gridRoot, "Line", center, size, _gridMaterial);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }

    /// <summary>編輯模式的工具（P7 第 5 步）。</summary>
    public enum RoadTool
    {
        /// <summary>選取：點模組、節點、路段；拖曳移動模組或節點。</summary>
        Select,

        /// <summary>畫路線：拖曳畫一條新路段。</summary>
        Draw,
    }

    /// <summary>選取的種類（一次只能選一種）。</summary>
    public enum SelectionKind
    {
        None,
        Module,
        Node,
        Edge,
    }
}
