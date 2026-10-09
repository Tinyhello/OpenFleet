using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using OpenFleet.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace OpenFleet.Unity
{
    /// <summary>
    /// P2 操作面板（UI Toolkit，介面全部用程式建立）：修改示範設定（批量、各站加工時間）、建立批次、查看批次目前的狀態與位置。
    /// 面板只透過 HTTP 讀寫後端，自己不保存任何業務狀態；批次清單以輪詢更新。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ProductionPanel : MonoBehaviour
    {
        [SerializeField] private FleetView fleetView;
        [SerializeField] private float refreshSeconds = 1f;

        private static readonly Color PanelColor = new Color(0.10f, 0.12f, 0.15f, 0.92f);
        private static readonly Color MutedColor = new Color(0.65f, 0.69f, 0.75f);
        private static readonly Color ErrorColor = new Color(1f, 0.55f, 0.45f);
        private const int MaxSliderSeconds = 120;
        private static readonly Color InputTextColor = new Color(0.08f, 0.09f, 0.11f);
        private static readonly Color OkColor = new Color(0.55f, 0.9f, 0.6f);

        private readonly List<(ProcessStepDto Step, SliderInt Field)> _stepFields = new List<(ProcessStepDto, SliderInt)>();
        private readonly Dictionary<string, string> _stationNames = new Dictionary<string, string>();
        private VisualElement _root;
        private VisualElement _panel;
        private VisualElement _stepRows;
        private VisualElement _lotRows;
        private SliderInt _lotQuantity;
        private Label _quantityHint;
        private Label _status;
        private ProductionSettingsDto _settings;

        /// <summary>滑鼠是否在面板上；攝影機用來避免捲動清單時同時縮放畫面。</summary>
        public static bool PointerOverPanel { get; private set; }

        private string Api => fleetView != null ? fleetView.ApiBaseUrl : "http://localhost:5229";

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
        private static void ApplyFont(VisualElement root)
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
            s.width = 360;
            s.maxHeight = Length.Percent(92);
            s.backgroundColor = PanelColor;
            s.color = Color.white;
            s.fontSize = 14;
            s.paddingTop = s.paddingBottom = 10;
            s.paddingLeft = s.paddingRight = 12;
            s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = 6;
            _root.Add(_panel);

            _panel.Add(Heading("示範設定"));
            _panel.Add(Hint("暫用數值，尚未定案；套用後存進後端資料庫。"));

            _lotQuantity = NumberField("批量（件）", 1, 30);
            _panel.Add(_lotQuantity);
            _quantityHint = Hint(string.Empty);
            _panel.Add(_quantityHint);

            _panel.Add(Hint("各站整批加工時間（秒）"));
            _stepRows = new VisualElement();
            _panel.Add(_stepRows);

            var settingButtons = Row();
            settingButtons.Add(ActionButton("套用", () => StartCoroutine(SaveSettings())));
            settingButtons.Add(ActionButton("重新載入", () => StartCoroutine(LoadSettings())));
            _panel.Add(settingButtons);

            _panel.Add(Heading("批次"));
            var lotButtons = Row();
            lotButtons.Add(ActionButton("建立批次", () => StartCoroutine(Post("/api/lots", "{}", "已建立批次"))));
            lotButtons.Add(ActionButton("清除示範資料", () => StartCoroutine(Post("/api/demo/reset", null, "已清除批次與搬運資料"))));
            _panel.Add(lotButtons);

            _status = Hint(string.Empty);
            _status.style.whiteSpace = WhiteSpace.Normal;
            _panel.Add(_status);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.maxHeight = 260;
            scroll.style.marginTop = 6;
            _lotRows = scroll.contentContainer;
            _panel.Add(scroll);
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
            }

            // 站點名稱用來把批次位置顯示成中文。
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
            _lotQuantity.highValue = settings.MaxLotQuantity;
            _lotQuantity.value = settings.LotQuantity;
            _quantityHint.text = $"1～{settings.MaxLotQuantity}（待加工料容量；目前不拆批）";

            _stepRows.Clear();
            _stepFields.Clear();
            foreach (var step in settings.Steps)
            {
                // 滑桿上限取 120 秒；後端已經存了更大的值時放寬上限，避免套用時被悄悄改小。
                var field = NumberField($"{step.Sequence}. {step.StationName}", 1, Mathf.Max(MaxSliderSeconds, step.DurationSeconds));
                field.value = step.DurationSeconds;
                _stepRows.Add(field);
                _stepFields.Add((step, field));
            }
        }

        private IEnumerator SaveSettings()
        {
            if (_settings == null)
            {
                SetStatus("尚未讀到設定，請先按「重新載入」。", ErrorColor);
                yield break;
            }

            _settings.LotQuantity = _lotQuantity.value;
            foreach (var (step, field) in _stepFields)
            {
                step.DurationSeconds = field.value;
            }

            using (var request = JsonRequest(UnityWebRequest.kHttpVerbPUT, "/api/settings", FleetJson.Serialize(_settings)))
            {
                yield return request.SendWebRequest();
                if (Succeeded(request, "套用設定"))
                {
                    ShowSettings(FleetJson.Deserialize<ProductionSettingsDto>(request.downloadHandler.text));
                    SetStatus("設定已套用。排隊中的批次開始加工時會採用新的時間。", OkColor);
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
                    SetStatus(successMessage + "。", OkColor);
                    yield return RefreshLots();
                }
            }
        }

        private IEnumerator RefreshLoop()
        {
            while (true)
            {
                yield return RefreshLots();
                yield return new WaitForSecondsRealtime(refreshSeconds);
            }
        }

        private IEnumerator RefreshLots()
        {
            using (var request = UnityWebRequest.Get($"{Api}/api/lots"))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    yield break;
                }

                var lots = FleetJson.Deserialize<LotDto[]>(request.downloadHandler.text);
                _lotRows.Clear();
                if (lots.Length == 0)
                {
                    _lotRows.Add(Hint("還沒有批次。"));
                }

                foreach (var lot in lots)
                {
                    var row = new Label($"{lot.LotNumber}　{lot.Quantity} 件\n{Describe(lot)}");
                    row.style.whiteSpace = WhiteSpace.Normal;
                    row.style.marginBottom = 6;
                    row.style.color = lot.Status == LotStatus.Staged ? MutedColor : Color.white;
                    _lotRows.Add(row);
                }
            }
        }

        private string Describe(LotDto lot)
        {
            var place = lot.VehicleId ?? (_stationNames.TryGetValue(lot.StationCode ?? string.Empty, out var name) ? name : lot.StationCode);
            switch (lot.Status)
            {
                case LotStatus.AwaitingTransport: return $"在 {place} 等待搬運";
                case LotStatus.InTransit: return $"在 {place} 車上運送中";
                case LotStatus.Queued: return $"在 {place} 排隊等待加工";
                case LotStatus.Processing: return $"在 {place} 加工中";
                case LotStatus.Staged: return $"已送達 {place} 暫存";
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

            var detail = request.responseCode == 400 ? ValidationMessage(request.downloadHandler?.text) : request.error;
            SetStatus($"{action}失敗：{detail}", ErrorColor);
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
            label.style.color = MutedColor;
            label.style.marginBottom = 4;
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
                input.style.color = InputTextColor;
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
            button.style.color = InputTextColor;
            button.style.height = 28;
            return button;
        }

        /// <summary>ASP.NET Core ValidationProblem 的回應格式。</summary>
        private sealed class ValidationProblem
        {
            public Dictionary<string, string[]> Errors { get; set; } = new Dictionary<string, string[]>();
        }
    }
}
