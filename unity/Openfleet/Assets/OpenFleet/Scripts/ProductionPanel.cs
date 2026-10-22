using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using OpenFleet.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using Colors = OpenFleet.Unity.PresentationController.Colors;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 操作面板（UI Toolkit，介面全部用程式建立）：修改示範設定（批量、各站加工時間與開爐條件、品管的不良率、貨車間隔）、開關測試模式、
    /// 下單與訂單的進度（ProductionPanel.Orders.cs）、查看缺車耽誤的時間、品管的累計結果與出貨的現況。
    /// 面板只透過 HTTP 讀寫後端，自己不保存任何業務狀態；訂單與缺車統計以輪詢更新。
    /// 由 PresentationController 在執行時建立。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed partial class ProductionPanel : MonoBehaviour
    {
        private const float RefreshSeconds = 1f;

        /// <summary>按「套用」時，把各工序滑桿上的值寫回要送出的設定。</summary>
        private readonly List<Action> _stepAppliers = new List<Action>();
        private readonly Dictionary<string, string> _stationNames = new Dictionary<string, string>();
        private VisualElement _root;
        private VisualElement _panel;
        private VisualElement _stepRows;
        private SliderInt _lotQuantity;
        private SliderInt _pieceHandlingSeconds;
        private SliderInt _agvHandoverSeconds;
        private SliderInt _toolChangeSeconds;
        private SliderInt _toolThreshold;
        private SliderInt _changeoverMinutes;
        /// <summary>測試倍率的上限，和後端的 ProductionService.MaxTestSpeed 相同。</summary>
        private const int MaxTestSpeed = 5000;

        private SliderInt _testHandoverSpeed;
        private SliderInt _testEmptyVehicleSpeed;
        private SliderInt _testLoadedVehicleSpeed;
        private SliderInt _defectPercent;
        private SliderInt _maxReworkCount;
        private SliderInt _reworkBatchMin;
        private Label _inspection;
        private SliderInt _truckMinutes;
        private SliderInt _testTruckSpeed;
        private Label _shipping;
        private Label _quantityHint;
        private Label _status;
        private Label _shortage;
        private Label _incident;
        private VisualElement _attention;
        private string _attentionShown = string.Empty;
        private ProductionSettingsDto _settings;
        private Button _testMode;

        /// <summary>滑鼠是否在面板上；攝影機用來避免捲動清單時同時縮放畫面。</summary>
        public static bool PointerOverPanel { get; private set; }

        private string Api => PresentationController.Instance.Fleet.ApiBaseUrl;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _root.Clear();
            // 讓沒有被面板蓋住的地方不攔截滑鼠。
            _root.pickingMode = PickingMode.Ignore;
            ApplyFont(_root);
            Build();
            StartCoroutine(LoadSettings());
            StartCoroutine(RefreshLoop());
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            PointerOverPanel = false;
        }

        private void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null || _panel == null || _root.panel == null)
            {
                PointerOverPanel = false;
                return;
            }

            // 螢幕座標原點在左下，面板座標原點在左上。
            var screen = mouse.position.ReadValue();
            var position = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(screen.x, Screen.height - screen.y));
            PointerOverPanel = _panel.worldBound.Contains(position);
        }

        /// <summary>
        /// UI Toolkit 的預設字型沒有中文字。和場景標籤一樣改用作業系統的中文字型；
        /// WebGL 沒有系統字型，屆時要改成隨專案附帶的字型。
        /// </summary>
        public static void ApplyFont(VisualElement root)
        {
            var font = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft JhengHei UI", "Microsoft JhengHei", "PingFang TC", "Noto Sans CJK TC" }, 16);
            if (font != null)
            {
                root.style.unityFontDefinition = new StyleFontDefinition(font);
            }
        }

        private void Build()
        {
            _panel = new VisualElement();
            var s = _panel.style;
            s.position = Position.Absolute;
            s.top = 10;
            s.right = 10;
            s.width = 380;
            s.maxHeight = Length.Percent(92);
            s.backgroundColor = Colors.Panel;
            s.color = Color.white;
            s.fontSize = 14;
            s.paddingTop = s.paddingBottom = 10;
            s.paddingLeft = s.paddingRight = 12;
            s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = 6;
            _root.Add(_panel);

            // 展開示範設定後內容比畫面高：整個面板在最大高度內捲動，各區塊不會被擠在一起。
            // 捲軸只在內容超過高度時出現；面板的寬度已經把捲軸算進去，出現時文字不會因此多換行。
            var panelScroll = new ScrollView(ScrollViewMode.Vertical);
            panelScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _panel.Add(panelScroll);
            var content = panelScroll.contentContainer;

            // 設定預設收合，平常只留批次清單，不擋住場景；點標題展開。
            var settings = new Foldout { text = "示範設定（點此展開）", value = false };
            settings.RegisterValueChangedCallback(e =>
            {
                if (e.target == settings)
                {
                    settings.text = e.newValue ? "示範設定" : "示範設定（點此展開）";
                    panelScroll.scrollOffset = Vector2.zero;
                }
            });
            settings.Q<Toggle>().style.marginLeft = 0;
            var title = settings.Q<Label>();
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            content.Add(settings);

            settings.Add(Hint("暫用數值，尚未定案；套用後存進後端資料庫。"));

            _lotQuantity = NumberField("批量（組）", 1, 300);
            settings.Add(_lotQuantity);
            _quantityHint = Hint(string.Empty);
            settings.Add(_quantityHint);

            // 機台取放：加工前取一件、加工後取出，各花這麼久。AGV 交接：一框整個交給站點或取走，和件數無關。
            _pieceHandlingSeconds = NumberField("機台取放（秒／件）", 1, 20);
            settings.Add(_pieceHandlingSeconds);
            _agvHandoverSeconds = NumberField("AGV 交接（秒／框）", 1, 120);
            settings.Add(_agvHandoverSeconds);
            _toolChangeSeconds = NumberField("換刀時間（秒）", 1, 300);
            settings.Add(_toolChangeSeconds);
            _toolThreshold = NumberField("叫刀門檻（剩餘件）", 1, 200);
            settings.Add(_toolThreshold);
            // 機床換線：粗加工、精加工接著做的那一框桿徑不同時，先換工控程式。
            _changeoverMinutes = NumberField("機床換線（分）", 1, 60);
            settings.Add(_changeoverMinutes);

            // 測試模式下車輛的倍率：交接、空車與載貨的車速（車速最多 5 倍，再快交通控制跟不上）；各站自己的倍率在下面每一站各一欄。
            // 倍率的上限和後端相同（5000）。
            _testHandoverSpeed = NumberField("測試：AGV 交接（×）", 1, MaxTestSpeed);
            settings.Add(_testHandoverSpeed);
            _testEmptyVehicleSpeed = NumberField("測試：空車車速（×）", 1, 5);
            settings.Add(_testEmptyVehicleSpeed);
            _testLoadedVehicleSpeed = NumberField("測試：載貨車速（×）", 1, 5);
            settings.Add(_testLoadedVehicleSpeed);

            // 品管：每一支以這個機率被判不良（三種原因機率相同）；同一支最多反工幾次，超過就報廢，0 表示不反工。
            _defectPercent = NumberField("品管不良率（%）", 0, 50);
            settings.Add(_defectPercent);
            _maxReworkCount = NumberField("反工上限（次）", 0, 3);
            settings.Add(_maxReworkCount);
            // 鍍層不良的反工品累積到這麼多支才重新電鍍；沒有其他料會再進電鍍時，不足的也放行。
            _reworkBatchMin = NumberField("反工重鍍下限（支）", 1, 300);
            settings.Add(_reworkBatchMin);

            // 出貨：貨車每隔這麼久來一次，把出貨站上的箱子載走；測試模式下用右邊的倍率加快。
            _truckMinutes = NumberField("貨車間隔（分）", 1, 240);
            settings.Add(_truckMinutes);
            _testTruckSpeed = NumberField("測試：貨車（×）", 1, MaxTestSpeed);
            settings.Add(_testTruckSpeed);

            _stepRows = new VisualElement();
            settings.Add(_stepRows);

            var settingButtons = Row();
            settingButtons.Add(ActionButton("套用", () => StartCoroutine(SaveSettings())));
            settingButtons.Add(ActionButton("重新載入", () => StartCoroutine(LoadSettings())));
            settings.Add(settingButtons);

            // 測試模式：每一站依自己的專用倍率加快（倍率在示範設定裡調整），車輛的交接與車速也依設定加快（使用者 2026-10-22）。
            // 沒有整體的時間加速（使用者 2026-10-10 取消）。
            var testRow = Row();
            _testMode = ActionButton("測試模式", () => StartCoroutine(ToggleTestMode()));
            testRow.Add(_testMode);
            content.Add(testRow);
            content.Add(Hint("開啟後各站依自己的倍率加快；AGV 交接與車速（空車、載貨）也依設定加快。"));

            // 重大事故：某個車型全部離線。平常隱藏，發生時用紅字顯示在最上面。
            _incident = new Label();
            _incident.style.whiteSpace = WhiteSpace.Normal;
            _incident.style.color = Colors.Incident;
            _incident.style.unityFontStyleAndWeight = FontStyle.Bold;
            _incident.style.marginTop = 6;
            _incident.style.display = DisplayStyle.None;
            content.Add(_incident);

            // 需要人工處理的搬運：失敗而且系統不能自己重派（物料可能在車上）。平常是空的。
            _attention = new VisualElement();
            content.Add(_attention);


            // 下單與訂單（P5）：建立批次改成下客戶訂單。
            BuildOrderForm(content);

            _status = Hint(string.Empty);
            _status.style.whiteSpace = WhiteSpace.Normal;
            content.Add(_status);

            BuildOrderList(content);

            // 缺車統計：車都在忙、叫不到車時，機台真的停下來的時間，按工序列出。
            content.Add(Heading("缺車耽誤"));
            _shortage = Hint("尚未讀到資料。");
            _shortage.style.whiteSpace = WhiteSpace.Normal;
            content.Add(_shortage);

            // 品管的累計結果：合格、反工（表面回拋光、鍍層回電鍍）、報廢（尺寸不良，或反工次數用完）。
            content.Add(Heading("品管"));
            _inspection = Hint("尚未讀到資料。");
            _inspection.style.whiteSpace = WhiteSpace.Normal;
            content.Add(_inspection);

            // 包裝與出貨：整張訂單做完才裝箱送到出貨站（或負責人同意的那一次分批）；貨車定時來，把出貨單的箱子都到齊的載走。
            content.Add(Heading("出貨"));
            _shipping = Hint("尚未讀到資料。");
            _shipping.style.whiteSpace = WhiteSpace.Normal;
            content.Add(_shipping);

            var resetRow = Row();
            resetRow.Add(ActionButton("清除示範資料", () => StartCoroutine(Post("/api/demo/reset", null, "已清除訂單、批次與搬運資料"))));
            content.Add(resetRow);
        }

        /// <summary>
        /// 開關測試模式：立刻送出，不必按「套用」。送的是上次從後端讀到的設定，只改這一個開關；
        /// 示範設定裡還沒套用的修改不會跟著送出。
        /// </summary>
        private IEnumerator ToggleTestMode()
        {
            if (_settings == null)
            {
                SetStatus("尚未讀到設定，請先按「重新載入」。", Colors.Error);
                yield break;
            }

            _settings.TestMode = !_settings.TestMode;
            using (var request = JsonRequest(UnityWebRequest.kHttpVerbPUT, "/api/settings", FleetJson.Serialize(_settings)))
            {
                yield return request.SendWebRequest();
                if (!Succeeded(request, "切換測試模式"))
                {
                    _settings.TestMode = !_settings.TestMode;
                    yield break;
                }

                ShowSettings(FleetJson.Deserialize<ProductionSettingsDto>(request.downloadHandler.text));
                SetStatus(_settings.TestMode ? "測試模式已開啟：各站、AGV 交接與空車車速依各自的倍率加快。" : "測試模式已關閉：恢復原本的時間。", Colors.Ok);
            }
        }

        /// <summary>顯示的是後端回報的狀態，不是剛按下的按鈕。</summary>
        private void ShowTestMode(bool on)
        {
            _testMode.text = on ? "測試模式：開（點此關閉）" : "測試模式：關（點此開啟）";
            _testMode.style.backgroundColor = on ? Colors.Selected : new StyleColor(StyleKeyword.Null);
            _testMode.style.unityFontStyleAndWeight = on ? FontStyle.Bold : FontStyle.Normal;
        }

        private IEnumerator LoadSettings()
        {
            using (var request = UnityWebRequest.Get($"{Api}/api/settings"))
            {
                yield return request.SendWebRequest();
                if (!Succeeded(request, "讀取設定"))
                {
                    yield break;
                }

                ShowSettings(FleetJson.Deserialize<ProductionSettingsDto>(request.downloadHandler.text));
                _orderQuantity.value = _settings.LotQuantity;
            }

            yield return LoadSpecs();

            // 站點名稱用來把料框的位置顯示成中文。
            using (var request = UnityWebRequest.Get($"{Api}/api/stations"))
            {
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    _stationNames.Clear();
                    foreach (var station in FleetJson.Deserialize<StationDto[]>(request.downloadHandler.text))
                    {
                        _stationNames[station.Code] = station.Name;
                    }
                }
            }
        }

        private void ShowSettings(ProductionSettingsDto settings)
        {
            _settings = settings;
            ShowTestMode(settings.TestMode);
            _lotQuantity.highValue = settings.MaxLotQuantity;
            _lotQuantity.value = settings.LotQuantity;
            _quantityHint.text = $"1～{settings.MaxLotQuantity} 組，一組是上沖、中模、下沖各一；一框 Φ30 裝 30 支、Φ24 裝 45 支（中模跟同組的上下沖相同），超過就分成多框";

            SetValue(_pieceHandlingSeconds, settings.PieceHandlingSeconds);
            SetValue(_agvHandoverSeconds, settings.AgvHandoverSeconds);
            SetValue(_toolChangeSeconds, settings.ToolChangeSeconds);
            SetValue(_toolThreshold, settings.ToolRequestThresholdPieces);
            SetValue(_changeoverMinutes, settings.ChangeoverSeconds / 60d);
            SetValue(_testHandoverSpeed, settings.TestHandoverSpeed);
            SetValue(_testEmptyVehicleSpeed, settings.TestEmptyVehicleSpeed);
            SetValue(_testLoadedVehicleSpeed, settings.TestLoadedVehicleSpeed);
            // 這兩個欄位 0 是有意義的值（不產生不良、不反工），不能用 SetValue（它會把 0 當成 1）。
            _defectPercent.value = Mathf.RoundToInt((float)(settings.DefectRate * 100d));
            _maxReworkCount.value = settings.MaxReworkCount;
            _reworkBatchMin.value = settings.ReworkBatchMinPieces;
            SetValue(_truckMinutes, settings.TruckIntervalSeconds / 60d);
            SetValue(_testTruckSpeed, settings.TestTruckSpeed);

            _stepRows.Clear();
            _stepAppliers.Clear();
            foreach (var item in settings.Steps)
            {
                var step = item;
                _stepRows.Add(Hint($"{step.Sequence}. {step.StationName}"));

                // 這一站的專用倍率，測試模式開啟時才有作用。
                var testSpeed = NumberField("測試倍率（×）", 1, MaxTestSpeed);
                SetValue(testSpeed, step.TestSpeed);
                _stepRows.Add(testSpeed);
                _stepAppliers.Add(() => step.TestSpeed = testSpeed.value);

                if (step.Kind == ProcessStepKind.Batch)
                {
                    AddBatchFields(step);
                    continue;
                }

                var seconds = NumberField(step.IsInspection ? "檢驗（秒／支）" : step.IsPacking ? "包裝（秒／支）" : "加工（秒／件）", 1, 120);
                SetValue(seconds, step.SecondsPerPiece);
                _stepRows.Add(seconds);
                _stepAppliers.Add(() => step.SecondsPerPiece = seconds.value);

                // 沒有刀具的工序（拋光）沒有這一欄。
                if (step.UsesTool)
                {
                    var toolLife = NumberField("刀具壽命（件）", 10, 1000);
                    SetValue(toolLife, step.ToolLifePieces);
                    _stepRows.Add(toolLife);
                    _stepAppliers.Add(() => step.ToolLifePieces = toolLife.value);
                }
            }
        }

        /// <summary>整爐工序：開爐下限、一爐上限（電鍍依桿徑各一個）、一爐的時間（分鐘）。</summary>
        private void AddBatchFields(ProcessStepDto step)
        {
            // 上限依桿徑不同的工序（電鍍）：兩種桿徑可以混裝，各占「支數 ÷ 自己的上限」。
            var perDiameter = step.BatchMaxPiecesRod24 > 0;
            var min = NumberField("開爐下限（支）", 1, 300);
            SetValue(min, step.BatchMinPieces);
            _stepRows.Add(min);
            var max = NumberField(perDiameter ? "一爐上限 Φ30（支）" : "一爐上限（支）", 1, 300);
            SetValue(max, step.BatchMaxPieces);
            _stepRows.Add(max);

            var minutes = NumberField($"{step.StageName ?? "處理"}（分）", 1, 720);
            SetValue(minutes, step.BatchSeconds / 60d);
            _stepAppliers.Add(() =>
            {
                step.BatchMinPieces = min.value;
                step.BatchMaxPieces = max.value;
                step.BatchSeconds = minutes.value * 60d;
            });

            if (perDiameter)
            {
                var thin = NumberField("一爐上限 Φ24（支）", 1, 300);
                SetValue(thin, step.BatchMaxPiecesRod24);
                _stepRows.Add(thin);
                _stepAppliers.Add(() => step.BatchMaxPiecesRod24 = thin.value);
            }

            _stepRows.Add(minutes);
        }

        /// <summary>
        /// 後端的值超出滑桿範圍時放寬範圍，避免「只是按了套用，數值卻被滑桿悄悄夾住」。
        /// 滑桿只能表示整數；後端若存了小數（例如測試用的值），顯示會四捨五入。
        /// </summary>
        private static void SetValue(SliderInt slider, double value)
        {
            var rounded = Mathf.Max(1, Mathf.RoundToInt((float)value));
            slider.highValue = Mathf.Max(slider.highValue, rounded);
            slider.lowValue = Mathf.Min(slider.lowValue, rounded);
            slider.value = rounded;
        }

        private IEnumerator SaveSettings()
        {
            if (_settings == null)
            {
                SetStatus("尚未讀到設定，請先按「重新載入」。", Colors.Error);
                yield break;
            }

            _settings.LotQuantity = _lotQuantity.value;
            _settings.PieceHandlingSeconds = _pieceHandlingSeconds.value;
            _settings.AgvHandoverSeconds = _agvHandoverSeconds.value;
            _settings.ToolChangeSeconds = _toolChangeSeconds.value;
            _settings.ToolRequestThresholdPieces = _toolThreshold.value;
            _settings.ChangeoverSeconds = _changeoverMinutes.value * 60d;
            _settings.TestHandoverSpeed = _testHandoverSpeed.value;
            _settings.TestEmptyVehicleSpeed = _testEmptyVehicleSpeed.value;
            _settings.TestLoadedVehicleSpeed = _testLoadedVehicleSpeed.value;
            _settings.DefectRate = _defectPercent.value / 100d;
            _settings.MaxReworkCount = _maxReworkCount.value;
            _settings.ReworkBatchMinPieces = _reworkBatchMin.value;
            _settings.TruckIntervalSeconds = _truckMinutes.value * 60d;
            _settings.TestTruckSpeed = _testTruckSpeed.value;
            foreach (var apply in _stepAppliers)
            {
                apply();
            }

            using (var request = JsonRequest(UnityWebRequest.kHttpVerbPUT, "/api/settings", FleetJson.Serialize(_settings)))
            {
                yield return request.SendWebRequest();
                if (Succeeded(request, "套用設定"))
                {
                    ShowSettings(FleetJson.Deserialize<ProductionSettingsDto>(request.downloadHandler.text));
                    SetStatus("設定已套用。已開始的加工與搬運不受影響，之後開始的才採用新值；測試倍率立刻生效。", Colors.Ok);
                }
            }
        }

        private IEnumerator Post(string path, string json, string successMessage)
        {
            using (var request = JsonRequest(UnityWebRequest.kHttpVerbPOST, path, json))
            {
                yield return request.SendWebRequest();
                if (Succeeded(request, successMessage))
                {
                    SetStatus(successMessage + "。", Colors.Ok);
                    yield return RefreshOrders();
                }
            }
        }

        private IEnumerator RefreshLoop()
        {
            while (true)
            {
                yield return RefreshOrders();
                yield return RefreshShortage();
                yield return RefreshInspection();
                yield return RefreshShipping();
                yield return RefreshAttention();
                yield return new WaitForSecondsRealtime(RefreshSeconds);
            }
        }

        /// <summary>
        /// 失敗、等人工處理的搬運。每一筆兩個按鈕：物料已放回取料站（系統重新派一趟），或已由人工送到卸料站（視同送達）。
        /// </summary>
        private IEnumerator RefreshAttention()
        {
            using (var request = UnityWebRequest.Get($"{Api}/api/transport-tasks"))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    yield break;
                }

                var waiting = new List<TransportTaskDto>();
                foreach (var task in FleetJson.Deserialize<TransportTaskDto[]>(request.downloadHandler.text))
                {
                    if (task.NeedsAttention)
                    {
                        waiting.Add(task);
                    }
                }

                // 內容沒變就不重建，否則每秒重建一次，按鈕會在按下去的瞬間被換掉。
                var key = string.Join(",", waiting.ConvertAll(t => t.Id.ToString()));
                if (key == _attentionShown)
                {
                    yield break;
                }

                _attentionShown = key;
                _attention.Clear();
                if (waiting.Count == 0)
                {
                    yield break;
                }

                var heading = Heading($"需要人工處理（{waiting.Count}）");
                heading.style.color = Colors.Incident;
                _attention.Add(heading);
                foreach (var task in waiting)
                {
                    var id = task.Id;
                    var what = task.Kind == TransportTaskKind.ToolDelivery ? "刀具" : $"{task.LotNumber} {task.Quantity} 件";
                    var row = new Label($"{what}：{StationName(task.FromStationCode)} → {StationName(task.ToStationCode)}（{task.VehicleId}）\n{task.FailureReason}");
                    row.style.whiteSpace = WhiteSpace.Normal;
                    row.style.fontSize = 12;
                    _attention.Add(row);

                    var buttons = Row();
                    buttons.Add(ActionButton("已放回取料站", () => StartCoroutine(Resolve(id, TaskResolution.ReturnToSource))));
                    buttons.Add(ActionButton("已送到卸料站", () => StartCoroutine(Resolve(id, TaskResolution.DeliverToDestination))));
                    buttons.style.marginBottom = 6;
                    _attention.Add(buttons);
                }
            }
        }

        private IEnumerator Resolve(Guid taskId, TaskResolution resolution)
        {
            var json = FleetJson.Serialize(new ResolveTaskRequest { Resolution = resolution });
            using (var request = JsonRequest(UnityWebRequest.kHttpVerbPOST, $"/api/transport-tasks/{taskId}/resolve", json))
            {
                yield return request.SendWebRequest();
                if (Succeeded(request, "人工處理"))
                {
                    SetStatus(resolution == TaskResolution.ReturnToSource ? "已記錄：物料放回取料站，系統會重新派一趟。" : "已記錄：物料已由人工送達。", Colors.Ok);
                    yield return RefreshAttention();
                    yield return RefreshOrders();
                }
            }
        }

        private string StationName(string code)
        {
            return _stationNames.TryGetValue(code ?? string.Empty, out var name) ? name : code;
        }

        private IEnumerator RefreshShortage()
        {
            using (var request = UnityWebRequest.Get($"{Api}/api/shortage"))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    yield break;
                }

                var shortage = FleetJson.Deserialize<VehicleShortageDto>(request.downloadHandler.text);
                var text = new StringBuilder();
                text.Append(shortage.WaitingTasks > 0 ? $"目前 {shortage.WaitingTasks} 個任務叫不到車" : "目前沒有任務在等車");
                text.Append($"；累計等車 {Duration(shortage.WaitSeconds)}（{shortage.WaitCount} 次）");
                foreach (var step in shortage.Steps)
                {
                    text.Append($"\n{step.Sequence}. {step.StationName}　停機 {Duration(step.DelaySeconds)}");
                    text.Append($"（等料 {Duration(step.WaitingForMaterialSeconds)}／成品未搬走 {Duration(step.OutputNotRemovedSeconds)}／等刀 {Duration(step.WaitingForToolSeconds)}）");
                    if (step.CurrentCause.HasValue && step.CurrentReason == ShortageReason.VehiclesBusy)
                    {
                        text.Append("　停機中");
                    }
                }

                _shortage.text = text.ToString();
                _shortage.style.color = shortage.WaitingTasks > 0 ? Colors.Shortage : Colors.Muted;
                ShowIncident(shortage);
            }
        }

        private IEnumerator RefreshInspection()
        {
            using (var request = UnityWebRequest.Get($"{Api}/api/inspection"))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    yield break;
                }

                var summary = FleetJson.Deserialize<InspectionSummaryDto>(request.downloadHandler.text);
                if (summary.Inspected == 0)
                {
                    _inspection.text = "還沒有檢驗紀錄。";
                    yield break;
                }

                _inspection.text =
                    $"已檢 {summary.Inspected} 支：合格 {summary.Passed}／反工 {summary.Reworked}／報廢 {summary.Scrapped}\n" +
                    $"不良原因：表面 {summary.SurfaceDefects}／鍍層 {summary.PlatingDefects}／尺寸 {summary.DimensionDefects}";
            }
        }

        private IEnumerator RefreshShipping()
        {
            using (var request = UnityWebRequest.Get($"{Api}/api/shipping"))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    yield break;
                }

                var summary = FleetJson.Deserialize<ShippingSummaryDto>(request.downloadHandler.text);
                var remaining = Mathf.Max(0, Mathf.CeilToInt((float)summary.TruckRemainingSeconds));
                _shipping.text =
                    $"下一班貨車：{remaining / 60} 分 {remaining % 60:00} 秒後\n" +
                    $"已包裝、等整張訂單到齊：{summary.PackedPieces} 支\n" +
                    $"在出貨站等貨車：{summary.WaitingBoxes} 箱／{summary.WaitingPieces} 支（下一班會載走 {summary.ReadyBoxes} 箱）\n" +
                    $"累計已出貨：{summary.ShippedBoxes} 箱／{summary.ShippedPieces} 支";
            }
        }

        /// <summary>
        /// 車輛全部離線是重大事故，和缺車分開顯示：事故進行中，或曾經因此耽誤過，才顯示這一段。
        /// </summary>
        private void ShowIncident(VehicleShortageDto shortage)
        {
            var offlineSeconds = 0d;
            foreach (var step in shortage.Steps)
            {
                offlineSeconds += step.OfflineSeconds;
            }

            var active = shortage.OfflineVehicleTypes.Count > 0;
            if (!active && shortage.OfflineWaitCount == 0)
            {
                _incident.style.display = DisplayStyle.None;
                return;
            }

            var text = new StringBuilder();
            if (active)
            {
                var types = new List<string>();
                foreach (var type in shortage.OfflineVehicleTypes)
                {
                    types.Add(type == VehicleType.Large ? "大型車" : type == VehicleType.Small ? "小型車" : type.ToString());
                }

                text.Append($"重大事故：{string.Join("、", types)}全部離線");
                if (shortage.OfflineWaitingTasks > 0)
                {
                    text.Append($"，{shortage.OfflineWaitingTasks} 個任務派不出去");
                }

                text.Append('\n');
            }

            text.Append($"離線事故累計：任務等待 {Duration(shortage.OfflineWaitSeconds)}（{shortage.OfflineWaitCount} 次），機台停機 {Duration(offlineSeconds)}");
            _incident.text = text.ToString();
            _incident.style.color = active ? Colors.Incident : Colors.Muted;
            _incident.style.display = DisplayStyle.Flex;
        }

        /// <summary>秒數顯示成「分:秒」。</summary>
        private static string Duration(double seconds)
        {
            var total = Mathf.Max(0, Mathf.RoundToInt((float)seconds));
            return $"{total / 60}:{total % 60:00}";
        }

        private string Describe(LotDto lot)
        {
            var place = lot.VehicleId ?? (_stationNames.TryGetValue(lot.StationCode ?? string.Empty, out var name) ? name : lot.StationCode);
            // 品管分出來要反工的料另外裝框，和同批次其他的框分開列。
            var rework = lot.IsMakeup ? "（補做）" : lot.ReworkCount > 0 && lot.Status != LotStatus.Staged ? "（反工）" : string.Empty;
            switch (lot.Status)
            {
                case LotStatus.AwaitingMaterial: return $"在 {place} 等料（圓棒不夠）{rework}";
                case LotStatus.AwaitingTransport: return $"在 {place} 等待搬運{rework}";
                case LotStatus.InTransit: return $"在 {place} 車上運送中{rework}";
                case LotStatus.Queued: return $"在 {place} 排隊等待加工{rework}";
                case LotStatus.Processing: return $"在 {place} 加工中{rework}";
                case LotStatus.Staged when lot.IsBox && lot.StationCode == "finished-store": return "已收進成品倉";
                case LotStatus.Staged: return lot.IsBox ? $"在 {place} 等貨車（同一張出貨單的箱子到齊才載）" : $"已送達 {place} 暫存";
                case LotStatus.Scrapped: return $"在 {place} 整框報廢";
                case LotStatus.Packed: return $"在 {place} 已包裝，等整張訂單到齊";
                case LotStatus.Boxed: return "已裝箱";
                case LotStatus.Shipped: return "已出貨";
                default: return lot.Status.ToString();
            }
        }

        private UnityWebRequest JsonRequest(string method, string path, string json)
        {
            var request = new UnityWebRequest($"{Api}{path}", method) { downloadHandler = new DownloadHandlerBuffer() };
            if (json != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.SetRequestHeader("Content-Type", "application/json");
            }

            return request;
        }

        /// <summary>失敗時把原因顯示在面板上；HTTP 400 會帶後端的驗證訊息。</summary>
        private bool Succeeded(UnityWebRequest request, string action)
        {
            if (request.result == UnityWebRequest.Result.Success)
            {
                return true;
            }

            // 400 帶驗證訊息；404、409 帶後端的說明（例如已經同意過分批、還沒有完整的組）。
            var detail = request.responseCode == 400 ? ValidationMessage(request.downloadHandler?.text)
                : request.responseCode == 404 || request.responseCode == 409 ? ProblemDetail(request.downloadHandler?.text) ?? request.error
                : request.error;
            SetStatus($"{action}失敗：{detail}", Colors.Error);
            Debug.LogWarning($"[OpenFleet] {action} failed: {request.responseCode} {request.error} {request.downloadHandler?.text}");
            return false;
        }

        private static string ValidationMessage(string body)
        {
            try
            {
                var problem = FleetJson.Deserialize<ValidationProblem>(body);
                var messages = new List<string>();
                foreach (var pair in problem.Errors)
                {
                    messages.AddRange(pair.Value);
                }

                return string.Join(" ", messages);
            }
            catch (Exception)
            {
                return body;
            }
        }

        private static string ProblemDetail(string body)
        {
            try
            {
                return FleetJson.Deserialize<Problem>(body)?.Detail;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void SetStatus(string message, Color color)
        {
            _status.text = message;
            _status.style.color = color;
        }

        private static Label Heading(string text)
        {
            var label = new Label(text);
            label.style.fontSize = 16;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 6;
            label.style.marginBottom = 2;
            return label;
        }

        private static Label Hint(string text)
        {
            var label = new Label(text);
            label.style.fontSize = 12;
            label.style.color = Colors.Muted;
            label.style.marginBottom = 4;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        /// <summary>
        /// 整數欄位：滑桿加輸入框。用滑鼠拖曳就能調整，不需要鍵盤；
        /// 中文輸入法開著時數字鍵會被輸入法攔截，只靠輸入框並不好用。
        /// </summary>
        private static SliderInt NumberField(string label, int low, int high)
        {
            var field = new SliderInt(label, low, high) { showInputField = true };
            field.style.marginBottom = 3;
            field.labelElement.style.color = Color.white;
            field.labelElement.style.minWidth = 110;
            // 面板的白色文字會繼承到輸入框，預設主題的輸入框是淺色底，所以另外指定深色字。
            var input = field.Q<TextField>();
            if (input != null)
            {
                input.style.color = Colors.InputText;
                input.style.minWidth = 48;
            }

            return field;
        }

        private static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 4;
            return row;
        }

        private static Button ActionButton(string text, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.style.flexGrow = 1;
            button.style.color = Colors.InputText;
            button.style.height = 28;
            return button;
        }

        /// <summary>ASP.NET Core ProblemDetails 的回應格式（只取說明）。</summary>
        private sealed class Problem
        {
            public string Detail { get; set; }
        }

        /// <summary>ASP.NET Core ValidationProblem 的回應格式。</summary>
        private sealed class ValidationProblem
        {
            public Dictionary<string, string[]> Errors { get; set; } = new Dictionary<string, string[]>();
        }
    }
}
