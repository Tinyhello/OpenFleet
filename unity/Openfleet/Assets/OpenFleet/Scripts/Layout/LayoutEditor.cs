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

        public LayoutModule SelectedModule => FindModule(_selected);

        /// <summary>正在擺放的種類；沒有在擺放是 null。</summary>
        public ModuleKind? Placing => _placing;

        public bool CanDeleteSelected => _session != null && _selected != null && _session.CanRemove(_selected);

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
            _selected = null;
            _dragging = null;
            EndPlacing();

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

            EndPlacing();
            _dragging = null;
            _session = null;
            _selected = null;
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

        public void Select(string code)
        {
            if (!Editing || code == _selected)
            {
                return;
            }

            var previous = _selected;
            _selected = FindModule(code) != null ? code : null;
            Restyle(previous);
            Restyle(_selected);
            Changed?.Invoke();
        }

        public void StartPlacing(ModuleKind kind)
        {
            if (!Editing || !_session.CanAdd(kind))
            {
                return;
            }

            EndPlacing();
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
            if (CanDeleteSelected && _session.RemoveModule(_selected))
            {
                _selected = null;
                Rebuild(false);
            }
        }

        public void Undo()
        {
            if (Editing && _dragging == null && _session.Undo())
            {
                AfterHistory();
            }
        }

        public void Redo()
        {
            if (Editing && _dragging == null && _session.Redo())
            {
                AfterHistory();
            }
        }

        private void AfterHistory()
        {
            if (FindModule(_selected) == null)
            {
                _selected = null;
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
        /// 點檢查結果的一條：選取它的第一個模組（沒有模組就看第一個節點、第一條路段）並把攝影機移過去。
        /// </summary>
        public void FocusIssue(LayoutIssueDto issue)
        {
            if (!Editing || issue == null)
            {
                return;
            }

            Vector3? point = null;
            var document = _session.Document;
            if (issue.ModuleCodes != null && issue.ModuleCodes.Count > 0 && FindModule(issue.ModuleCodes[0]) is { } module)
            {
                Select(module.Code);
                var rect = LayoutGeometry.Footprint(module);
                point = new Vector3((float)(rect.MinX + rect.MaxX) / 2f, 0f, (float)(rect.MinZ + rect.MaxZ) / 2f);
            }
            else
            {
                var nodeCode = issue.NodeCodes != null && issue.NodeCodes.Count > 0 ? issue.NodeCodes[0]
                    : issue.Edges != null && issue.Edges.Count > 0 ? issue.Edges[0].From : null;
                foreach (var node in document.RoadNodes)
                {
                    if (node.Code == nodeCode)
                    {
                        point = new Vector3((float)node.X, 0f, (float)node.Z);
                        break;
                    }
                }
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

            if (_dragging != null)
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

            if (_placing != null)
            {
                UpdateGhost(!overUi && hasPoint, x, z);
                if (!overUi && hasPoint && mouse.leftButton.wasPressedThisFrame)
                {
                    PlaceGhost();
                }

                return;
            }

            if (!overUi && hasPoint && mouse.leftButton.wasPressedThisFrame)
            {
                // 點場景時讓面板的輸入框放開焦點，鍵盤快捷鍵才會恢復。
                ProductionPanel.ClearFocus();
                var module = HitModule(x, z);
                Select(module?.Code);
                if (module != null)
                {
                    BeginDrag(module, screen, x, z);
                }
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
}
