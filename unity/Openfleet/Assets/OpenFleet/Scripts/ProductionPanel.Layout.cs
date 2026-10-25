using System;
using System.Collections;
using System.Collections.Generic;
using OpenFleet.Contracts;
using OpenFleet.Layout;
using UnityEngine;
using UnityEngine.UIElements;
using Colors = OpenFleet.Unity.PresentationController.Colors;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 操作面板的「佈局」頁（P7 第 4 步）：進入與離開編輯、新增模組、選取的模組、廠房大小、檢查結果、佈局存檔與套用。
    /// 編輯的狀態與滑鼠鍵盤操作在 <see cref="LayoutEditor"/>，這裡只負責畫面與呼叫它，以及佈局存檔的 HTTP 呼叫
    /// （<see cref="LayoutClient"/>）。數值只用滑桿與按鈕調整（注音輸入法打不進數字）；用不到的按鈕停用而不是隱藏。
    /// </summary>
    public sealed partial class ProductionPanel
    {
        /// <summary>「新增模組」各組的按鈕。機台與倉儲站、裝飾物的名稱取自模組目錄；機台的名稱要比目錄的工序名稱更像一台設備，所以另外寫。</summary>
        private static readonly (string Heading, (ModuleKind Kind, string Text)[] Items)[] AddGroups =
        {
            ("機台", new[]
            {
                (ModuleKind.Cutting, "切料"),
                (ModuleKind.RoughLathe, "粗加工車床"),
                (ModuleKind.FineLathe, "精加工車床"),
                (ModuleKind.QuenchFurnace, "淬火爐"),
                (ModuleKind.TemperFurnace, "回火爐"),
                (ModuleKind.PlatingLine, "電鍍槽"),
                (ModuleKind.Polisher, "拋光機"),
                (ModuleKind.InspectionBench, "品管站"),
                (ModuleKind.PackingBench, "包裝站"),
            }),
            ("倉儲（各一個，只能移動、旋轉）", new[]
            {
                (ModuleKind.RawMaterialStore, "原料倉"),
                (ModuleKind.FinishedGoodsStore, "成品倉"),
                (ModuleKind.ShippingDock, "出貨"),
                (ModuleKind.ToolStore, "刀具庫"),
            }),
            ("AGV（放一個車位就多一台車）", new[]
            {
                (ModuleKind.AgvBayLarge, "大型 AGV"),
                (ModuleKind.AgvBaySmall, "小型 AGV"),
            }),
            ("裝飾", new[]
            {
                (ModuleKind.MiscStore, "雜物倉庫"),
                (ModuleKind.CarrierStorage, "載具存放區"),
            }),
        };

        /// <summary>廠房大小滑桿：寬 40～100 m、深 24～60 m，每格 0.4 m（和 LayoutRules、LayoutEditSession.FloorStep 相同）。</summary>
        private const int FloorWidthSteps = 150, FloorDepthSteps = 90;

        /// <summary>檢查結果最多列出幾條（太多時只列前面的，總數照實顯示）。Claude 暫定。</summary>
        private const int MaxIssuesShown = 40;

        /// <summary>編輯模式的說明文字，依目前的工具切換。</summary>
        private const string SelectToolHelp = "編輯時場景畫的是草稿，車輛與進度條先隱藏，工廠在後端照常運轉。" +
            "選取工具：點模組、路口（白點）或路段選取；按住拖曳移動模組或路口（路口放到另一個路口上會合併）；" +
            "R 旋轉模組、Delete 刪除、Esc 取消、Ctrl+Z 復原、Ctrl+Y 重做。";

        private const string DrawToolHelp = "畫路線：按下的位置是起點，拖到終點放開就畫出路段；" +
            "起點與終點會吸附到 0.6 m 內的路口、0.4 m 內的路段、0.2 m 格點；按住 Shift 鎖定水平或垂直；" +
            "短於 1 m 不會畫；Esc 取消這一筆。畫路線時左鍵不會選取或拖曳模組；右鍵與中鍵拖曳仍是移動攝影機。";

        private static ProductionPanel _layoutPanel;

        private readonly Dictionary<ModuleKind, Button> _addButtons = new Dictionary<ModuleKind, Button>();
        private VisualElement _layoutPage;
        private Button _layoutTab;
        private LayoutEditor _editor;
        private LayoutController _layoutController;
        private VisualElement _prompt;
        private Label _promptText;
        private Button _promptYes;
        private Button _promptNo;
        private Action _promptYesAction;
        private Label _layoutMessage;
        private Button _editToggle;
        private Button _undoButton;
        private Button _redoButton;
        private Label _editName;
        private Label _editDirty;
        private Label _editHint;
        private Button _toolSelect;
        private Button _toolDraw;
        private VisualElement _selectionBox;
        private string _selectionKey;
        private SliderInt _floorWidth;
        private SliderInt _floorDepth;
        private Label _issueSummary;
        private VisualElement _issueList;
        private LayoutValidationDto _issuesShown;
        private VisualElement _layoutList;
        private TextField _saveName;
        private Button _loadButton;
        private Button _saveButton;
        private Button _saveAsButton;
        private Button _deleteButton;
        private Button _applyButton;
        private Button _defaultButton;
        private Label _applyHint;
        private List<LayoutSummaryDto> _layouts = new List<LayoutSummaryDto>();
        private Guid? _selectedLayoutId;
        private bool _layoutBusy;
        private bool _layoutRefreshPending;
        private bool _layoutListLoading;

        // ---- 生命週期（ProductionPanel.cs 的 OnEnable／OnDisable／Update 呼叫） ----

        private void OnLayoutPanelEnabled()
        {
            _layoutPanel = this;
            var presentation = PresentationController.Instance;
            _layoutController = presentation.GetComponent<LayoutController>();
            // 場景是舊的、沒有帶 LayoutEditor 時（正常是 FleetView 的 RequireComponent 帶上）補上，不至於整個頁面不能用。
            _editor = presentation.GetComponent<LayoutEditor>() ?? presentation.gameObject.AddComponent<LayoutEditor>();
            _editor.Changed += OnEditorChanged;
            _layoutController.LayoutBuilt += OnLayoutBuilt;
            _layoutBusy = false;
            StartCoroutine(LoadLayoutList());
            RefreshLayoutPage();
        }

        private void OnLayoutPanelDisabled()
        {
            if (_layoutPanel == this)
            {
                _layoutPanel = null;
            }

            if (_editor != null)
            {
                _editor.Changed -= OnEditorChanged;
            }

            if (_layoutController != null)
            {
                _layoutController.LayoutBuilt -= OnLayoutBuilt;
            }
        }

        /// <summary>讓面板的輸入框放開焦點（點場景時由 LayoutEditor 呼叫），鍵盤快捷鍵才會恢復。</summary>
        public static void ClearFocus()
        {
            _layoutPanel?._root?.panel?.focusController?.focusedElement?.Blur();
        }

        private void OnEditorChanged()
        {
            // 同一個 frame 內可能發出很多次（拖滑桿），合併成一次更新。
            _layoutRefreshPending = true;
        }

        private void OnLayoutBuilt()
        {
            // 使用中的佈局換了（自己套用、或別處套用）：清單的「使用中」跟著變。
            StartCoroutine(LoadLayoutList());
        }

        private void UpdateLayoutPage()
        {
            if (_layoutRefreshPending)
            {
                _layoutRefreshPending = false;
                RefreshLayoutPage();
            }
        }

        private void OnLayoutPageShown()
        {
            StartCoroutine(LoadLayoutList());
            RefreshLayoutPage();
        }

        // ---- 畫面 ----

        private void BuildLayoutPage(VisualElement page)
        {
            // 確認區塊與訊息放在最上面（不用系統對話框）：離開編輯、載入、刪除、回到預設佈局的確認，
            // 以及套用遇到在製品（409）時的「清空示範資料並套用」。
            _prompt = new VisualElement();
            var p = _prompt.style;
            p.display = DisplayStyle.None;
            p.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
            p.paddingTop = p.paddingBottom = 6;
            p.paddingLeft = p.paddingRight = 8;
            p.marginBottom = 6;
            p.borderTopLeftRadius = p.borderTopRightRadius = p.borderBottomLeftRadius = p.borderBottomRightRadius = 4;
            p.borderTopWidth = p.borderBottomWidth = p.borderLeftWidth = p.borderRightWidth = 1;
            p.borderTopColor = p.borderBottomColor = p.borderLeftColor = p.borderRightColor = Colors.Selected;
            _promptText = new Label();
            _promptText.style.whiteSpace = WhiteSpace.Normal;
            _prompt.Add(_promptText);
            var promptRow = Row();
            _promptYes = ActionButton("確定", () => AnswerPrompt(true));
            promptRow.Add(_promptYes);
            _promptNo = ActionButton("取消", () => AnswerPrompt(false));
            promptRow.Add(_promptNo);
            _prompt.Add(promptRow);
            page.Add(_prompt);

            _layoutMessage = Hint(string.Empty);
            _layoutMessage.style.display = DisplayStyle.None;
            page.Add(_layoutMessage);

            // (1) 編輯狀態
            var editRow = Row();
            editRow.style.marginTop = 0;
            _editToggle = ActionButton("進入編輯", ToggleEdit);
            editRow.Add(_editToggle);
            _undoButton = ActionButton("復原", () => _editor.Undo());
            _undoButton.style.flexGrow = 0;
            _undoButton.style.width = 60;
            _undoButton.style.marginLeft = 4;
            editRow.Add(_undoButton);
            _redoButton = ActionButton("重做", () => _editor.Redo());
            _redoButton.style.flexGrow = 0;
            _redoButton.style.width = 60;
            editRow.Add(_redoButton);
            page.Add(editRow);

            var nameRow = Row();
            _editName = new Label();
            _editName.style.whiteSpace = WhiteSpace.Normal;
            _editName.style.flexShrink = 1;
            nameRow.Add(_editName);
            _editDirty = new Label("　＊未儲存");
            _editDirty.style.color = Colors.Selected;
            _editDirty.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameRow.Add(_editDirty);
            page.Add(nameRow);

            // 工具列（P7 第 5 步）：選取、畫路線。只在編輯中可用，目前的工具反白。
            var toolRow = Row();
            _toolSelect = ActionButton("選取", () => _editor.SetTool(RoadTool.Select));
            toolRow.Add(_toolSelect);
            _toolDraw = ActionButton("畫路線", () => _editor.SetTool(RoadTool.Draw));
            toolRow.Add(_toolDraw);
            page.Add(toolRow);

            _editHint = Hint(string.Empty);
            _editHint.style.marginTop = 4;
            page.Add(_editHint);

            // (2) 新增模組
            var add = new Foldout { text = "新增模組", value = true };
            add.Q<Toggle>().style.marginLeft = 0;
            var addTitle = add.Q<Label>();
            addTitle.style.fontSize = 16;
            addTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            addTitle.style.color = Color.white;
            add.Add(Hint("按下按鈕後模組跟著滑鼠，在場景點一下放下（R 旋轉、Esc 取消）；放下就自動接路。重疊或超出廠房也放得下，只是會被檢查結果標紅。"));
            foreach (var (heading, items) in AddGroups)
            {
                var sub = Hint(heading);
                sub.style.marginTop = 4;
                add.Add(sub);
                var wrap = WrapRow();
                foreach (var (kind, text) in items)
                {
                    var k = kind;
                    var button = ActionButton(text, () => _editor.StartPlacing(k));
                    button.style.minWidth = 104;
                    _addButtons[kind] = button;
                    wrap.Add(button);
                }

                add.Add(wrap);
            }

            page.Add(add);

            // (3) 選取的模組
            page.Add(Heading("選取"));
            _selectionBox = new VisualElement();
            page.Add(_selectionBox);

            // (4) 廠房大小
            page.Add(Heading("廠房大小"));
            _floorWidth = FloorSlider("寬度", FloorWidthSteps);
            page.Add(_floorWidth);
            _floorDepth = FloorSlider("深度", FloorDepthSteps);
            page.Add(_floorDepth);
            page.Add(Hint("每格 0.4 m，以廠房中心往兩邊縮放；大門固定在西牆中間。"));

            // (5) 檢查結果
            page.Add(Heading("檢查結果"));
            _issueSummary = new Label();
            _issueSummary.style.whiteSpace = WhiteSpace.Normal;
            page.Add(_issueSummary);
            _issueList = new VisualElement();
            page.Add(_issueList);

            // (6) 佈局存檔
            page.Add(Heading("佈局存檔"));
            _layoutList = new VisualElement();
            page.Add(_layoutList);

            var fileRow = WrapRow();
            _loadButton = ActionButton("載入", LoadSelected);
            _saveButton = ActionButton("儲存", () => StartCoroutine(SaveRoutine()));
            _saveAsButton = ActionButton("另存新佈局", () => StartCoroutine(SaveAsRoutine()));
            _deleteButton = ActionButton("刪除", DeleteSelected);
            foreach (var button in new[] { _loadButton, _saveButton, _saveAsButton, _deleteButton })
            {
                button.style.minWidth = 78;
                fileRow.Add(button);
            }

            page.Add(fileRow);

            // 另存的名稱：可以不填，自動用「佈局 N」（N 取最小沒用過的）。注音輸入法打不進數字，所以不能只靠輸入。
            _saveName = Text("新佈局名稱", string.Empty);
            _saveName.style.marginTop = 4;
            page.Add(_saveName);
            page.Add(Hint("名稱可以不填，另存時自動取「佈局 N」。"));

            var applyRow = WrapRow();
            _applyButton = ActionButton("套用", ApplyClicked);
            _defaultButton = ActionButton("回到預設佈局", DefaultClicked);
            applyRow.Add(_applyButton);
            applyRow.Add(_defaultButton);
            page.Add(applyRow);
            _applyHint = Hint(string.Empty);
            _applyHint.style.marginTop = 4;
            page.Add(_applyHint);
            page.Add(Hint("套用會清空生產資料（訂單、批次、搬運），AGV 回到各自的車位；有在製品時會先提醒。預設佈局不能修改，要改請另存。"));
        }

        private SliderInt FloorSlider(string label, int high)
        {
            var slider = new SliderInt(label, 0, high);
            slider.style.marginBottom = 3;
            slider.labelElement.style.color = Color.white;
            slider.labelElement.style.minWidth = 110;
            slider.RegisterValueChangedCallback(e => OnFloorSlider());
            return slider;
        }

        private static VisualElement WrapRow()
        {
            var row = Row();
            row.style.flexWrap = Wrap.Wrap;
            return row;
        }

        private void OnFloorSlider()
        {
            if (_editor == null || !_editor.Editing || _suppressFloorSlider)
            {
                return;
            }

            _editor.SetFloorSize(FloorFromStep(_floorWidth.value, LayoutRules.MinFloorWidth), FloorFromStep(_floorDepth.value, LayoutRules.MinFloorDepth));
        }

        private bool _suppressFloorSlider;

        private static double FloorFromStep(int step, double min) => Math.Round(min + step * LayoutEditSession.FloorStep, 2);

        private static int StepFromFloor(double size, double min) => (int)Math.Round((size - min) / LayoutEditSession.FloorStep);

        /// <summary>依編輯器與後端的狀態更新整頁：按鈕的停用、各區塊的內容。</summary>
        private void RefreshLayoutPage()
        {
            if (_layoutPage == null || _editor == null)
            {
                return;
            }

            var editing = _editor.Editing;
            var enabled = !_layoutBusy;

            // (1)
            _editToggle.text = editing ? "離開編輯" : "進入編輯";
            _editToggle.SetEnabled(enabled && (editing || _layoutController.HasLayout));
            Highlight(_editToggle, editing, Colors.Selected);
            _undoButton.SetEnabled(enabled && editing && _editor.CanUndo);
            _redoButton.SetEnabled(enabled && editing && _editor.CanRedo);
            _editName.text = editing ? $"編輯中：{_editor.SourceName}" : $"使用中：{_layoutController.Active?.Name ?? "（尚未讀到）"}{(_layouts.Exists(l => l.IsActive && l.HasUnappliedChanges) ? "（有未套用的修改）" : string.Empty)}";
            _editDirty.style.display = editing && _editor.IsDirty ? DisplayStyle.Flex : DisplayStyle.None;
            _editHint.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            var drawing = editing && _editor.Tool == RoadTool.Draw;
            _toolSelect.SetEnabled(enabled && editing);
            _toolDraw.SetEnabled(enabled && editing);
            Highlight(_toolSelect, editing && !drawing, Colors.Selected);
            Highlight(_toolDraw, drawing, Colors.Selected);
            _editHint.text = drawing ? DrawToolHelp : SelectToolHelp;

            // (2)
            foreach (var group in AddGroups)
            {
                foreach (var (kind, text) in group.Items)
                {
                    var button = _addButtons[kind];
                    button.text = $"{text}（{_editor.CountOf(kind)}）";
                    button.SetEnabled(enabled && editing && _editor.CanAdd(kind));
                    Highlight(button, editing && _editor.Placing == kind, Colors.Selected);
                }
            }

            RefreshSelection(editing, enabled);

            // (4)
            var floor = editing ? _editor.Document.Floor : _layoutController.Active?.Document?.Floor;
            _floorWidth.SetEnabled(enabled && editing);
            _floorDepth.SetEnabled(enabled && editing);
            if (floor != null)
            {
                var width = floor.MaxX - floor.MinX;
                var depth = floor.MaxZ - floor.MinZ;
                _suppressFloorSlider = true;
                _floorWidth.SetValueWithoutNotify(StepFromFloor(width, LayoutRules.MinFloorWidth));
                _floorDepth.SetValueWithoutNotify(StepFromFloor(depth, LayoutRules.MinFloorDepth));
                _suppressFloorSlider = false;
                _floorWidth.label = $"寬度 {width:0.0} m";
                _floorDepth.label = $"深度 {depth:0.0} m";
            }

            RefreshIssues(editing);
            RefreshLayoutFiles(editing, enabled);
        }

        private void RefreshSelection(bool editing, bool enabled)
        {
            var kind = editing ? _editor.SelectionType : SelectionKind.None;
            var module = kind == SelectionKind.Module ? _editor.SelectedModule : null;
            var node = kind == SelectionKind.Node ? _editor.SelectedNode : null;
            var edge = kind == SelectionKind.Edge ? _editor.SelectedEdge : null;
            var key = kind switch
            {
                SelectionKind.Module when module != null => $"M|{module.Code}|{module.X}|{module.Z}|{module.Rotation}|{enabled}|{_editor.CanDeleteSelected}|{_editor.Document.Modules.Count}",
                SelectionKind.Node when node != null => $"N|{node.Code}|{node.X}|{node.Z}|{enabled}|{_editor.CanDeleteSelected}|{_editor.EdgeCountOf(node.Code)}|{_editor.SelectedNodeIsHandoff}",
                SelectionKind.Edge when edge != null => $"E|{edge.From}|{edge.To}|{enabled}|{_editor.CanDeleteSelected}|{FindRoadNode(edge.From)?.X}|{FindRoadNode(edge.From)?.Z}|{FindRoadNode(edge.To)?.X}|{FindRoadNode(edge.To)?.Z}",
                _ => editing ? "-|edit" : "-",
            };
            if (key == _selectionKey)
            {
                return;
            }

            _selectionKey = key;
            _selectionBox.Clear();
            var document = editing ? _editor.Document : null;
            if (module != null)
            {
                var name = new Label($"{ModuleNaming.DisplayName(document, module)}　{ModuleNaming.EnglishName(document, module)}");
                name.style.unityFontStyleAndWeight = FontStyle.Bold;
                name.style.whiteSpace = WhiteSpace.Normal;
                _selectionBox.Add(name);
                _selectionBox.Add(Hint($"代碼 {module.Code}　位置 X {module.X:0.0}、Z {module.Z:0.0} m　朝向 {module.Rotation}°"));

                var row = Row();
                var rotate = ActionButton("旋轉 90°", () => _editor.RotateSelected());
                rotate.SetEnabled(enabled);
                row.Add(rotate);
                var delete = ActionButton("刪除", () => _editor.DeleteSelected());
                delete.SetEnabled(enabled && _editor.CanDeleteSelected);
                row.Add(delete);
                _selectionBox.Add(row);
                if (ModuleCatalog.IsFacility(module.Kind))
                {
                    _selectionBox.Add(Hint("倉儲站只能移動、旋轉，不能刪除。"));
                }

                return;
            }

            if (node != null)
            {
                var handoff = _editor.SelectedNodeIsHandoff;
                var name = new Label($"路口 {node.Code}{(handoff ? "（模組交接點）" : string.Empty)}");
                name.style.unityFontStyleAndWeight = FontStyle.Bold;
                name.style.whiteSpace = WhiteSpace.Normal;
                _selectionBox.Add(name);
                _selectionBox.Add(Hint($"位置 X {node.X:0.0}、Z {node.Z:0.0} m　連著 {_editor.EdgeCountOf(node.Code)} 條路段　{(handoff ? "交接點" : "一般路口")}"));

                var row = Row();
                var delete = ActionButton("刪除", () => _editor.DeleteSelected());
                delete.SetEnabled(enabled && _editor.CanDeleteSelected);
                row.Add(delete);
                _selectionBox.Add(row);
                _selectionBox.Add(Hint(handoff
                    ? "交接點跟著模組移動；要移動請拖曳模組。交接點不能刪除，要拆掉接路請刪它的支線（點路段再刪除）。"
                    : "按住拖曳可移動路口（放到另一個路口上會合併）；刪除會連同連著的路段一起刪。"));
                return;
            }

            if (edge != null)
            {
                var a = FindRoadNode(edge.From);
                var b = FindRoadNode(edge.To);
                var length = a != null && b != null ? Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z)) : 0d;
                var name = new Label($"路段 {edge.From} ～ {edge.To}");
                name.style.unityFontStyleAndWeight = FontStyle.Bold;
                name.style.whiteSpace = WhiteSpace.Normal;
                _selectionBox.Add(name);
                _selectionBox.Add(Hint($"長度 {length:0.0} m"));

                var row = Row();
                var delete = ActionButton("刪除", () => _editor.DeleteSelected());
                delete.SetEnabled(enabled && _editor.CanDeleteSelected);
                row.Add(delete);
                _selectionBox.Add(row);
                _selectionBox.Add(Hint("刪除只拿掉這一條路段；兩端的路口變成沒有路段連著時一併移除（交接點的節點會留下）。"));
                return;
            }

            _selectionBox.Add(Hint(editing ? "（點選場景中的模組、路口或路段；按住可拖曳移動）" : "（進入編輯後可選取模組、路口或路段）"));
        }

        private LayoutRoadNode FindRoadNode(string code)
        {
            foreach (var node in _editor.Document.RoadNodes)
            {
                if (node.Code == code)
                {
                    return node;
                }
            }

            return null;
        }

        private void RefreshIssues(bool editing)
        {
            if (!editing)
            {
                _issuesShown = null;
                _issueSummary.text = "（進入編輯後顯示）";
                _issueSummary.style.color = Colors.Muted;
                _issueList.Clear();
                return;
            }

            _issueSummary.text = $"{_editor.ErrorCount} 個錯誤、{_editor.WarningCount} 個警告";
            _issueSummary.style.color = _editor.ErrorCount > 0 ? Colors.Error : Colors.Ok;

            // 檢查結果沒變（只是選取、按鈕狀態變了）就不重建清單。
            if (ReferenceEquals(_issuesShown, _editor.Validation))
            {
                return;
            }

            _issuesShown = _editor.Validation;
            _issueList.Clear();
            var shown = 0;
            foreach (var item in _editor.Validation.Issues)
            {
                if (shown++ >= MaxIssuesShown)
                {
                    _issueList.Add(Hint($"…還有 {_editor.Validation.Issues.Count - MaxIssuesShown} 條沒有列出"));
                    break;
                }

                var issue = item;
                var error = issue.Severity == LayoutIssueSeverity.Error;
                var button = ListButton($"{(error ? "錯誤" : "警告")}：{issue.Message}", () => _editor.FocusIssue(issue));
                // 按鈕底色是淺色，文字用深色（紅、黃的字看不清楚）；嚴重程度靠左邊的色條。
                button.style.borderLeftWidth = 5;
                button.style.borderLeftColor = error ? Colors.LayoutError : Colors.LayoutSelected;
                _issueList.Add(button);
            }

            if (shown > 0)
            {
                _issueList.Add(Hint("點一條會選取相關的模組、路口或路段並把畫面移過去。"));
            }
        }

        private void RefreshLayoutFiles(bool editing, bool enabled)
        {
            _layoutList.Clear();
            foreach (var item in _layouts)
            {
                var layout = item;
                var tags = (layout.IsActive ? "〔使用中〕" : string.Empty) + (layout.IsDefault ? "〔預設〕" : string.Empty)
                    + (layout.HasUnappliedChanges ? "〔有未套用的修改〕" : string.Empty)
                    + (editing && _editor.SourceId == layout.Id ? "〔編輯中〕" : string.Empty);
                var button = ListButton($"{layout.Name}{tags}", () => SelectLayout(layout.Id));
                Highlight(button, layout.Id == _selectedLayoutId, Colors.Selected);
                _layoutList.Add(button);
            }

            if (_layouts.Count == 0)
            {
                _layoutList.Add(Hint("（還沒讀到佈局清單）"));
            }

            var selected = Find(_selectedLayoutId);
            _loadButton.SetEnabled(enabled && selected != null);
            _saveButton.SetEnabled(enabled && editing && !_editor.SourceIsDefault && _editor.IsDirty && _editor.SourceId.HasValue);
            _saveAsButton.SetEnabled(enabled && editing);
            _deleteButton.SetEnabled(enabled && selected != null && !selected.IsActive && !selected.IsDefault
                && !(editing && _editor.SourceId == selected.Id));

            var canApply = CanApply(editing, out var hint);
            _applyButton.SetEnabled(enabled && canApply);
            _applyHint.text = hint;
            var defaultEntry = _layouts.Find(l => l.IsDefault);
            _defaultButton.SetEnabled(enabled && defaultEntry != null && !(defaultEntry.IsActive && !defaultEntry.HasUnappliedChanges));
        }

        /// <summary>
        /// 套用的對象與能不能套用：編輯中是草稿（有修改先儲存再套用），沒在編輯是清單裡選的那一份。
        /// 預設佈局有修改時不能套用（保守的決定：請先另存，免得把預設佈局的修改悄悄存進新的名稱）。
        /// </summary>
        private bool CanApply(bool editing, out string hint)
        {
            hint = string.Empty;
            if (editing)
            {
                if (_editor.ErrorCount > 0)
                {
                    hint = "佈局有錯誤，不能套用。";
                    return false;
                }

                if (_editor.SourceIsDefault && _editor.IsDirty)
                {
                    hint = "預設佈局有修改，請先另存";
                    return false;
                }

                var entry = Find(_editor.SourceId);
                if (entry == null)
                {
                    hint = "找不到這份佈局（清單還在讀取，或已被刪除），請先另存。";
                    return false;
                }

                if (entry.IsActive && !_editor.IsDirty && !entry.HasUnappliedChanges)
                {
                    hint = "這份已經是使用中的佈局。";
                    return false;
                }

                hint = _editor.IsDirty ? $"套用「{entry.Name}」：有修改，會先儲存再套用。" : $"套用「{entry.Name}」。";
                return true;
            }

            var selected = Find(_selectedLayoutId);
            if (selected == null)
            {
                hint = "請先在清單選一份佈局。";
                return false;
            }

            if (selected.IsActive && !selected.HasUnappliedChanges)
            {
                hint = "這份已經是使用中的佈局。";
                return false;
            }

            hint = $"套用「{selected.Name}」。";
            return true;
        }

        private LayoutSummaryDto Find(Guid? id) => id.HasValue ? _layouts.Find(l => l.Id == id.Value) : null;

        private static Button ListButton(string text, Action onClick)
        {
            var button = ActionButton(text, onClick);
            button.style.height = StyleKeyword.Auto;
            button.style.minHeight = 26;
            button.style.unityTextAlign = TextAnchor.MiddleLeft;
            button.style.whiteSpace = WhiteSpace.Normal;
            button.style.marginLeft = button.style.marginRight = 0;
            return button;
        }

        // ---- 確認區塊與訊息 ----

        private void ShowPrompt(string text, string yesText, Action yes, string noText = "取消")
        {
            _promptText.text = text;
            _promptYes.text = yesText;
            _promptNo.text = noText;
            _promptYesAction = yes;
            _prompt.style.display = DisplayStyle.Flex;
            _panelScroll.scrollOffset = Vector2.zero;
        }

        private void AnswerPrompt(bool yes)
        {
            var action = _promptYesAction;
            HidePrompt();
            if (yes)
            {
                action?.Invoke();
            }
        }

        private void HidePrompt()
        {
            _promptYesAction = null;
            _prompt.style.display = DisplayStyle.None;
        }

        private void ShowLayoutMessage(string message, Color color)
        {
            _layoutMessage.text = message;
            _layoutMessage.style.color = color;
            _layoutMessage.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
            // 結果顯示在頁面最上面，捲回去讓使用者看得到。
            _panelScroll.scrollOffset = Vector2.zero;
        }

        // ---- 操作 ----

        private void ToggleEdit()
        {
            ShowLayoutMessage(string.Empty, Colors.Muted);
            if (!_editor.Editing)
            {
                StartCoroutine(EnterEditRoutine());
                return;
            }

            if (_editor.IsDirty)
            {
                ShowPrompt("有未儲存的修改，離開編輯會捨棄。", "捨棄並離開", () => _editor.Exit());
                return;
            }

            _editor.Exit();
        }

        /// <summary>
        /// 進入編輯。使用中的佈局有存了還沒套用的修改時，草稿從存檔版本開始（那是使用者最新的工作）；
        /// 否則從場景正在顯示的使用中版本開始。一般場景畫的仍是使用中（執行中）的版本。
        /// </summary>
        private IEnumerator EnterEditRoutine()
        {
            var active = _layoutController.Active;
            var layout = active;
            if (active != null && _layoutController.Client != null)
            {
                _layoutBusy = true;
                RefreshLayoutPage();
                LayoutResponse<LayoutDto> response = null;
                yield return _layoutController.Client.Get(active.Id, r => response = r);
                _layoutBusy = false;
                if (response.Ok && response.Value != null && response.Value.HasUnappliedChanges)
                {
                    layout = response.Value;
                }
                else if (!response.Ok)
                {
                    ShowLayoutMessage("讀取存檔版本失敗，改用使用中的版本：" + response.Message, Colors.Error);
                }
            }

            if (!_editor.Enter(layout))
            {
                ShowLayoutMessage("還沒有佈局可以編輯（佈局尚未載入）。", Colors.Error);
            }

            RefreshLayoutPage();
        }

        private void SelectLayout(Guid id)
        {
            _selectedLayoutId = id;
            RefreshLayoutPage();
        }

        private void LoadSelected()
        {
            var selected = Find(_selectedLayoutId);
            if (selected == null)
            {
                return;
            }

            ShowLayoutMessage(string.Empty, Colors.Muted);
            if (_editor.Editing && _editor.IsDirty)
            {
                ShowPrompt($"有未儲存的修改，載入「{selected.Name}」會捨棄它們。", "捨棄並載入", () => StartCoroutine(LoadRoutine(selected.Id)));
                return;
            }

            StartCoroutine(LoadRoutine(selected.Id));
        }

        private IEnumerator LoadRoutine(Guid id)
        {
            _layoutBusy = true;
            RefreshLayoutPage();
            LayoutResponse<LayoutDto> response = null;
            yield return _layoutController.Client.Get(id, r => response = r);
            _layoutBusy = false;
            if (!response.Ok)
            {
                ShowLayoutMessage("載入失敗：" + response.Message, Colors.Error);
            }
            else if (!_editor.Enter(response.Value))
            {
                ShowLayoutMessage("無法開始編輯（佈局尚未載入）。", Colors.Error);
            }

            RefreshLayoutPage();
        }

        private IEnumerator LoadLayoutList()
        {
            if (_layoutListLoading || _layoutController == null || _layoutController.Client == null)
            {
                yield break;
            }

            _layoutListLoading = true;
            LayoutResponse<List<LayoutSummaryDto>> response = null;
            yield return _layoutController.Client.List(r => response = r);
            _layoutListLoading = false;
            if (response.Ok && response.Value != null)
            {
                _layouts = response.Value;
                if (Find(_selectedLayoutId) == null)
                {
                    var active = _layouts.Find(l => l.IsActive);
                    _selectedLayoutId = active?.Id ?? (_layouts.Count > 0 ? _layouts[0].Id : (Guid?)null);
                }
            }
            else if (_layoutPage.resolvedStyle.display != DisplayStyle.None)
            {
                ShowLayoutMessage("讀取佈局清單失敗：" + response.Message, Colors.Error);
            }

            RefreshLayoutPage();
        }

        /// <summary>儲存（PUT）：覆蓋這份佈局。預設佈局不能覆蓋（按鈕停用，請另存）。</summary>
        private IEnumerator SaveRoutine()
        {
            if (!_editor.Editing || !_editor.SourceId.HasValue || _editor.SourceIsDefault)
            {
                yield break;
            }

            _layoutBusy = true;
            RefreshLayoutPage();
            var saved = false;
            yield return SaveCore(ok => saved = ok);
            _layoutBusy = false;
            if (saved)
            {
                ShowLayoutMessage($"已儲存「{_editor.SourceName}」。", Colors.Ok);
                yield return LoadLayoutList();
            }

            RefreshLayoutPage();
        }

        private IEnumerator SaveCore(Action<bool> done)
        {
            var id = _editor.SourceId.Value;
            var name = _editor.SourceName;
            LayoutResponse<SaveLayoutResponse> response = null;
            yield return _layoutController.Client.Update(id, name, _editor.Document, r => response = r);
            if (!response.Ok)
            {
                ShowLayoutMessage("儲存失敗：" + response.Message, Colors.Error);
                done(false);
                yield break;
            }

            _editor.MarkSaved(response.Value.Layout.Id, response.Value.Layout.Name, response.Value.Layout.IsDefault);
            done(true);
        }

        /// <summary>另存新佈局（POST）：名稱沒填就用「佈局 N」（N 取最小沒用過的）；成功後草稿改為這份新佈局。</summary>
        private IEnumerator SaveAsRoutine()
        {
            if (!_editor.Editing)
            {
                yield break;
            }

            var name = (_saveName.value ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                name = NextAutoName();
            }

            _layoutBusy = true;
            RefreshLayoutPage();
            LayoutResponse<SaveLayoutResponse> response = null;
            yield return _layoutController.Client.Create(name, _editor.Document, r => response = r);
            _layoutBusy = false;
            if (!response.Ok)
            {
                ShowLayoutMessage("另存失敗：" + response.Message, Colors.Error);
            }
            else
            {
                var layout = response.Value.Layout;
                _editor.MarkSaved(layout.Id, layout.Name, layout.IsDefault);
                _selectedLayoutId = layout.Id;
                _saveName.SetValueWithoutNotify(string.Empty);
                ShowLayoutMessage($"已另存為「{layout.Name}」。", Colors.Ok);
                yield return LoadLayoutList();
            }

            RefreshLayoutPage();
        }

        /// <summary>「佈局 N」：N 取最小沒用過的正整數。</summary>
        private string NextAutoName()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var layout in _layouts)
            {
                used.Add(layout.Name);
            }

            var n = 1;
            while (used.Contains($"佈局 {n}"))
            {
                n++;
            }

            return $"佈局 {n}";
        }

        private void DeleteSelected()
        {
            var selected = Find(_selectedLayoutId);
            if (selected == null || selected.IsActive || selected.IsDefault)
            {
                return;
            }

            ShowLayoutMessage(string.Empty, Colors.Muted);
            ShowPrompt($"刪除「{selected.Name}」？刪除後無法復原。", "刪除", () => StartCoroutine(DeleteRoutine(selected)));
        }

        private IEnumerator DeleteRoutine(LayoutSummaryDto layout)
        {
            _layoutBusy = true;
            RefreshLayoutPage();
            LayoutResponse<Nothing> response = null;
            yield return _layoutController.Client.Delete(layout.Id, r => response = r);
            _layoutBusy = false;
            if (!response.Ok)
            {
                ShowLayoutMessage("刪除失敗：" + response.Message, Colors.Error);
            }
            else
            {
                if (_selectedLayoutId == layout.Id)
                {
                    _selectedLayoutId = null;
                }

                ShowLayoutMessage($"已刪除「{layout.Name}」。", Colors.Ok);
                yield return LoadLayoutList();
            }

            RefreshLayoutPage();
        }

        private void ApplyClicked()
        {
            ShowLayoutMessage(string.Empty, Colors.Muted);
            if (_editor.Editing)
            {
                StartCoroutine(ApplyDraftRoutine());
                return;
            }

            var selected = Find(_selectedLayoutId);
            if (selected != null)
            {
                StartCoroutine(ApplyRoutine(selected.Id, false));
            }
        }

        /// <summary>編輯中的套用：草稿有修改先儲存（PUT），存成功才套用。</summary>
        private IEnumerator ApplyDraftRoutine()
        {
            if (!_editor.SourceId.HasValue)
            {
                yield break;
            }

            if (_editor.IsDirty)
            {
                _layoutBusy = true;
                RefreshLayoutPage();
                var saved = false;
                yield return SaveCore(ok => saved = ok);
                _layoutBusy = false;
                if (!saved)
                {
                    RefreshLayoutPage();
                    yield break;
                }
            }

            yield return ApplyRoutine(_editor.SourceId.Value, false);
        }

        private void DefaultClicked()
        {
            var entry = _layouts.Find(l => l.IsDefault);
            if (entry == null)
            {
                return;
            }

            ShowLayoutMessage(string.Empty, Colors.Muted);
            if (_editor.Editing && _editor.IsDirty)
            {
                ShowPrompt("編輯中有未儲存的修改，回到預設佈局會捨棄它們。", "捨棄並套用預設佈局", () => StartCoroutine(ApplyRoutine(entry.Id, false)));
                return;
            }

            StartCoroutine(ApplyRoutine(entry.Id, false));
        }

        /// <summary>
        /// 套用一份已存的佈局。工廠還有在製品（409）時顯示後端的說明與「清空示範資料並套用」；
        /// 成功後離開編輯、通知 LayoutController 重新載入使用中的佈局。
        /// </summary>
        private IEnumerator ApplyRoutine(Guid id, bool resetDemo)
        {
            _layoutBusy = true;
            RefreshLayoutPage();
            LayoutResponse<LayoutDto> response = null;
            yield return _layoutController.Client.Apply(id, resetDemo, r => response = r);
            _layoutBusy = false;

            if (response.Ok)
            {
                if (_editor.Editing)
                {
                    _editor.Exit(false);
                }

                _layoutController.Reload();
                ShowLayoutMessage($"已套用「{response.Value.Name}」。", Colors.Ok);
                yield return LoadLayoutList();
            }
            else if (response.Busy != null)
            {
                ShowPrompt(response.Busy.Message, "清空示範資料並套用", () => StartCoroutine(ApplyRoutine(id, true)));
            }
            else
            {
                ShowLayoutMessage("套用失敗：" + response.Message, Colors.Error);
            }

            RefreshLayoutPage();
        }
    }
}
