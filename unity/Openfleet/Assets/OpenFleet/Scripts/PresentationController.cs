using System;
using System.Collections.Generic;
using OpenFleet.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 表演用的控制器：畫面上的物件與顏色都由這裡管理，其他腳本不在 Inspector 掛參考。
    /// 1. 物件：AGV Prefab、模組 Prefab、環境材質與 UI 設定從 Resources 載入；場景裡的站點由 LayoutController 依佈局建好後交給 <see cref="SetStations"/>，攝影機用型別找到；操作面板與進度條在執行時建立。
    /// 2. 顏色：連線狀態、進度條、路徑虛線、面板的顏色集中在 <see cref="Colors"/>。
    /// 這裡只管「怎麼顯示」，不保存也不推算業務狀態；狀態來自後端（FleetView、StatusOverlay、ProductionPanel 各自取得）。
    /// 掛在場景的 OpenFleet 物件上（由選單 OpenFleet/Build Factory Scene 產生），不需要設定任何欄位。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class PresentationController : MonoBehaviour
    {
        /// <summary>Resources 底下的路徑；Build Factory Scene 產生資源時用同一組常數。</summary>
        public const string PrefabFolder = "Prefabs";
        public const string LargeVehiclePrefab = "AGV_Large";
        public const string SmallVehiclePrefab = "AGV_Small";
        public const string UnknownVehiclePrefab = "AGV_Unknown";
        public const string PanelSettingsPath = "UI/OpenFleetPanel";

        /// <summary>每種模組（<see cref="ModuleKind"/>）一個 Prefab，檔名是 <see cref="ModulePrefabName"/>。</summary>
        public const string ModulePrefabFolder = "Prefabs/Modules";

        /// <summary>執行時建立環境與路網要用的材質清單（<see cref="LayoutMaterials"/> 資產）。</summary>
        public const string LayoutMaterialsPath = "Layout/LayoutMaterials";

        public static string ModulePrefabName(ModuleKind kind) => "Module_" + kind;

        /// <summary>畫面上用到的顏色。要調整配色只改這裡。</summary>
        public static class Colors
        {
            // 車輛的燈條與警示燈
            public static readonly Color Online = new Color(0.2f, 0.8f, 0.3f);
            public static readonly Color Offline = new Color(0.45f, 0.45f, 0.45f);

            /// <summary>連線中的燈條發光強度（顏色的倍數）。</summary>
            public const float StatusEmission = 2f;

            // 場景物件上方的進度條
            public static readonly Color OverlayBack = new Color(0.08f, 0.09f, 0.11f, 0.82f);
            public static readonly Color BarTrack = new Color(1f, 1f, 1f, 0.16f);
            public static readonly Color Inbound = new Color(0.36f, 0.62f, 0.95f);
            public static readonly Color Outbound = new Color(0.45f, 0.82f, 0.5f);
            public static readonly Color Machining = new Color(0.98f, 0.72f, 0.25f);
            public static readonly Color Tool = new Color(0.78f, 0.6f, 0.95f);
            public static readonly Color ToolChange = new Color(0.98f, 0.45f, 0.4f);
            public static readonly Color Handover = new Color(0.35f, 0.85f, 0.9f);

            /// <summary>測試模式專用：爐前等待的進度（等滿就開爐，使用者 2026-10-23）。和排程頁的加速配色同一色系。</summary>
            public static readonly Color TestWait = new Color(0.35f, 0.85f, 0.95f);

            /// <summary>機台因缺車（車都在忙）停下來。</summary>
            public static readonly Color Shortage = new Color(0.95f, 0.3f, 0.55f);

            /// <summary>重大事故：車輛全部離線。</summary>
            public static readonly Color Incident = new Color(1f, 0.2f, 0.15f);

            /// <summary>刀具已到壽命，停機等新刀送到。</summary>
            public static readonly Color WaitingForTool = new Color(0.85f, 0.5f, 0.2f);

            // 路徑虛線：亮的是已放行，暗的是還在等路段預約
            public static readonly Color Route = new Color(1f, 0.92f, 0.35f, 0.95f);
            public static readonly Color RouteWaiting = new Color(1f, 0.92f, 0.35f, 0.3f);

            // 操作面板
            public static readonly Color Panel = new Color(0.10f, 0.12f, 0.15f, 0.92f);
            public static readonly Color Muted = new Color(0.65f, 0.69f, 0.75f);
            public static readonly Color Error = new Color(1f, 0.55f, 0.45f);
            public static readonly Color Ok = new Color(0.55f, 0.9f, 0.6f);
            public static readonly Color Selected = new Color(0.98f, 0.78f, 0.35f);

            // 佈局編輯（P7 第 4 步）：占地框線與地面墊片的顏色。顏色是 Claude 暫定的，要調整只改這裡。
            /// <summary>選取的模組。</summary>
            public static readonly Color LayoutSelected = new Color(1f, 0.85f, 0.2f);

            /// <summary>檢查結果有錯誤的模組。</summary>
            public static readonly Color LayoutError = new Color(1f, 0.25f, 0.2f);

            /// <summary>擺放中或拖曳中：這個位置可以放（在廠房內、沒有重疊）。</summary>
            public static readonly Color PlaceOk = new Color(0.25f, 0.9f, 0.4f);

            /// <summary>擺放中或拖曳中：這個位置不能放（超出廠房或和別的模組重疊）；放下去仍然允許，會在檢查結果裡報錯。</summary>
            public static readonly Color PlaceBlocked = new Color(1f, 0.25f, 0.2f);

            /// <summary>編輯模式地面的 1 m 格線。</summary>
            public static readonly Color Grid = new Color(0.52f, 0.57f, 0.63f);

            /// <summary>路網的節點（第 5 步畫路線時使用；這一步先定義，節點小圓點目前仍用場景材質）。</summary>
            public static readonly Color Node = new Color(0.95f, 0.97f, 1f);

            /// <summary>急單（排在所有訂單前面）的標示。</summary>
            public static readonly Color Rush = new Color(1f, 0.62f, 0.3f);
            public static readonly Color InputText = new Color(0.08f, 0.09f, 0.11f);

            /// <summary>
            /// 排程頁的配色。正常速度與加速的預估各一組，切換時整頁一起換色（使用者 2026-10-23），一眼看得出看的是哪一份。
            /// </summary>
            public static readonly SchedulePalette ScheduleNormal = new SchedulePalette(
                back: new Color(1f, 1f, 1f, 0.04f),
                border: new Color(1f, 1f, 1f, 0.18f),
                heading: Color.white,
                text: new Color(0.92f, 0.94f, 0.96f),
                muted: Muted,
                rush: Rush,
                onTime: new Color(0.55f, 0.9f, 0.6f),
                late: new Color(1f, 0.45f, 0.4f),
                blocked: new Color(0.95f, 0.8f, 0.35f));

            public static readonly SchedulePalette ScheduleAccelerated = new SchedulePalette(
                back: new Color(0.1f, 0.32f, 0.42f, 0.55f),
                border: new Color(0.35f, 0.85f, 0.95f, 0.9f),
                heading: new Color(0.5f, 0.92f, 1f),
                text: new Color(0.85f, 0.97f, 1f),
                muted: new Color(0.55f, 0.78f, 0.85f),
                rush: new Color(1f, 0.7f, 0.4f),
                onTime: new Color(0.5f, 0.95f, 0.8f),
                late: new Color(1f, 0.55f, 0.6f),
                blocked: new Color(1f, 0.85f, 0.45f));
        }

        /// <summary>排程頁的一組配色。</summary>
        public sealed class SchedulePalette
        {
            public SchedulePalette(Color back, Color border, Color heading, Color text, Color muted, Color rush, Color onTime, Color late, Color blocked)
            {
                Back = back;
                Border = border;
                Heading = heading;
                Text = text;
                Muted = muted;
                Rush = rush;
                OnTime = onTime;
                Late = late;
                Blocked = blocked;
            }

            /// <summary>整頁的底色與外框。</summary>
            public Color Back { get; }
            public Color Border { get; }
            public Color Heading { get; }
            public Color Text { get; }
            public Color Muted { get; }
            public Color Rush { get; }

            /// <summary>趕得上交期／趕不上交期。</summary>
            public Color OnTime { get; }
            public Color Late { get; }

            /// <summary>算不出來（等料、爐湊不滿）。</summary>
            public Color Blocked { get; }
        }

        /// <summary>
        /// 介面大小的級距（使用者調整的部分）。實際的縮放＝依畫面高度的倍數 × 這個值：
        /// 畫面高 900 像素時是 1 倍，1080p 約 1.2 倍、1440p 約 1.6 倍，比 900 像素小的畫面不再縮小。
        /// </summary>
        private static readonly float[] UiSizeSteps = { 0.8f, 0.9f, 1f, 1.15f, 1.3f, 1.5f, 1.75f, 2f, 2.5f };
        private const int DefaultUiSizeStep = 2;
        private const float UiReferenceHeight = 900f;
        private const string UiSizeKey = "OpenFleet.UiSizeStep";

        private readonly SortedDictionary<string, VehicleView> _vehicles = new SortedDictionary<string, VehicleView>();
        private readonly Dictionary<string, StationAnchor> _stations = new Dictionary<string, StationAnchor>();
        private readonly Dictionary<VehicleType, VehicleView> _prefabs = new Dictionary<VehicleType, VehicleView>();
        private readonly Dictionary<ModuleKind, GameObject> _modulePrefabs = new Dictionary<ModuleKind, GameObject>();
        private LayoutMaterials _layoutMaterials;

        /// <summary>執行時用的 PanelSettings 複本：改縮放時不會改到專案裡的資產。</summary>
        private PanelSettings _panelSettings;
        private int _uiSizeStep = DefaultUiSizeStep;
        private int _screenHeight;

        public static PresentationController Instance { get; private set; }

        /// <summary>
        /// 佈局編輯中（P7 第 4 步）：場景畫的是草稿，車輛與機台、車輛上的進度條、路徑虛線都先隱藏（工廠在後端照常運轉）。
        /// 由 LayoutEditor 設定；設定時車輛一起切換顯示，編輯中才出現的車直接建成隱藏。
        /// </summary>
        public bool LayoutEditing { get; private set; }

        /// <summary>編輯狀態改變；StatusOverlay 據此隱藏或恢復進度條與路徑虛線。</summary>
        public event Action LayoutEditingChanged;

        /// <summary>
        /// 目前的介面縮放倍數：操作面板、機台上的進度條（UI Toolkit）與左上的車輛清單（IMGUI）都照這個倍數放大。
        /// </summary>
        public static float UiScale { get; private set; } = 1f;

        /// <summary>使用者調整的介面大小（100% 是依畫面高度的預設）。</summary>
        public string UiSizeText => $"{Mathf.RoundToInt(UiSizeSteps[_uiSizeStep] * 100f)}%";

        /// <summary>後端連線設定與車輛狀態的來源；面板共用它的 API 位址。</summary>
        public FleetView Fleet { get; private set; }

        public FactoryCameraController CameraController { get; private set; }

        /// <summary>目前畫面上的車輛，依 ID 排序（唯讀）。</summary>
        public IEnumerable<VehicleView> Vehicles => _vehicles.Values;

        public int VehicleCount => _vehicles.Count;

        /// <summary>場景中的站點，以後端的站點代碼為鍵。佈局還沒建好時是空的。</summary>
        public IReadOnlyDictionary<string, StationAnchor> Stations => _stations;

        /// <summary>
        /// 站點換了（佈局重建，或第一次建好）。依站點建立的畫面元件（進度條）要在這時丟掉舊的；
        /// 事件發出時 <see cref="Stations"/> 已經是新的。
        /// </summary>
        public event Action StationsChanged;

        /// <summary>
        /// 以 <c>-hide-panels</c> 啟動時不顯示車輛面板與操作面板（錄影、截圖用）；機台與車輛上的進度條照常顯示。
        /// </summary>
        public static bool PanelsHidden { get; } = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-hide-panels") >= 0;

        private void Awake()
        {
            Instance = this;
            Fleet = GetComponent<FleetView>();
            CameraController = FindAnyObjectByType<FactoryCameraController>();

            // 兩個 UIDocument：進度條排在操作面板的下層。
            var panelSettings = Resources.Load<PanelSettings>(PanelSettingsPath);
            if (panelSettings == null)
            {
                Debug.LogError($"[OpenFleet] Resources/{PanelSettingsPath} not found; run OpenFleet/Build Factory Scene.");
                return;
            }

            _panelSettings = Instantiate(panelSettings);
            _panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
            _uiSizeStep = Mathf.Clamp(PlayerPrefs.GetInt(UiSizeKey, DefaultUiSizeStep), 0, UiSizeSteps.Length - 1);
            ApplyUiScale();

            CreateDocument<StatusOverlay>("Status Overlay", _panelSettings, -1);
            if (!PanelsHidden)
            {
                CreateDocument<ProductionPanel>("Production Panel", _panelSettings, 0);
            }
        }

        private void Update()
        {
            // 視窗大小改了就重新依畫面高度計算。
            if (Screen.height != _screenHeight)
            {
                ApplyUiScale();
            }

            // Ctrl＋＝／Ctrl＋－ 調整介面大小，Ctrl＋0 回到預設。
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.ctrlKey.isPressed)
            {
                return;
            }

            if (keyboard.equalsKey.wasPressedThisFrame || keyboard.numpadPlusKey.wasPressedThisFrame)
            {
                ChangeUiSize(1);
            }
            else if (keyboard.minusKey.wasPressedThisFrame || keyboard.numpadMinusKey.wasPressedThisFrame)
            {
                ChangeUiSize(-1);
            }
            else if (keyboard.digit0Key.wasPressedThisFrame || keyboard.numpad0Key.wasPressedThisFrame)
            {
                ChangeUiSize(DefaultUiSizeStep - _uiSizeStep);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            if (_panelSettings != null)
            {
                Destroy(_panelSettings);
            }
        }

        /// <summary>介面大小調大（正數）或調小（負數）幾級；記在本機，下次開啟沿用。</summary>
        public void ChangeUiSize(int steps)
        {
            var step = Mathf.Clamp(_uiSizeStep + steps, 0, UiSizeSteps.Length - 1);
            if (step == _uiSizeStep)
            {
                return;
            }

            _uiSizeStep = step;
            PlayerPrefs.SetInt(UiSizeKey, step);
            PlayerPrefs.Save();
            ApplyUiScale();
        }

        private void ApplyUiScale()
        {
            _screenHeight = Screen.height;
            UiScale = Mathf.Max(1f, Screen.height / UiReferenceHeight) * UiSizeSteps[_uiSizeStep];
            if (_panelSettings != null)
            {
                _panelSettings.scale = UiScale;
            }
        }

        public bool TryGetVehicle(string vehicleId, out VehicleView view) => _vehicles.TryGetValue(vehicleId, out view);

        /// <summary>進入或離開佈局編輯：車輛一起隱藏或恢復，並通知進度條與路徑虛線。</summary>
        public void SetLayoutEditing(bool editing)
        {
            if (LayoutEditing == editing)
            {
                return;
            }

            LayoutEditing = editing;
            foreach (var view in _vehicles.Values)
            {
                view.gameObject.SetActive(!editing);
            }

            LayoutEditingChanged?.Invoke();
        }

        /// <summary>依車型從 Resources 載入 Prefab 並建立一台車。</summary>
        public VehicleView SpawnVehicle(string vehicleId, VehicleType type)
        {
            var view = VehicleView.Create(PrefabFor(type), vehicleId, type, transform);
            view.gameObject.SetActive(!LayoutEditing);
            _vehicles.Add(vehicleId, view);
            return view;
        }

        public void RemoveVehicle(string vehicleId)
        {
            if (_vehicles.TryGetValue(vehicleId, out var view))
            {
                _vehicles.Remove(vehicleId);
                Destroy(view.gameObject);
            }
        }

        private VehicleView PrefabFor(VehicleType type)
        {
            if (_prefabs.TryGetValue(type, out var prefab))
            {
                return prefab;
            }

            string name;
            switch (type)
            {
                case VehicleType.Large: name = LargeVehiclePrefab; break;
                case VehicleType.Small: name = SmallVehiclePrefab; break;
                // 後端設定裡沒有車型的車輛。
                default: name = UnknownVehiclePrefab; break;
            }

            prefab = Resources.Load<VehicleView>($"{PrefabFolder}/{name}");
            if (prefab == null)
            {
                Debug.LogError($"[OpenFleet] Resources/{PrefabFolder}/{name} not found; run OpenFleet/Build Factory Scene.");
            }

            _prefabs[type] = prefab;
            return prefab;
        }

        /// <summary>執行時建立環境與路網要用的材質；資產不存在或欄位沒填滿時回傳 null 並記錄錯誤。</summary>
        public LayoutMaterials LayoutMaterials
        {
            get
            {
                if (_layoutMaterials == null)
                {
                    _layoutMaterials = Resources.Load<LayoutMaterials>(LayoutMaterialsPath);
                    if (_layoutMaterials == null || !_layoutMaterials.IsComplete)
                    {
                        Debug.LogError($"[OpenFleet] Resources/{LayoutMaterialsPath} is missing or incomplete; run OpenFleet/Build Factory Scene.");
                        _layoutMaterials = null;
                    }
                }

                return _layoutMaterials;
            }
        }

        /// <summary>依模組種類從 Resources 載入 Prefab；找不到時記錄錯誤並回傳 null（每種只記一次）。</summary>
        public GameObject ModulePrefab(ModuleKind kind)
        {
            if (_modulePrefabs.TryGetValue(kind, out var prefab))
            {
                return prefab;
            }

            var name = ModulePrefabName(kind);
            prefab = Resources.Load<GameObject>($"{ModulePrefabFolder}/{name}");
            if (prefab == null)
            {
                Debug.LogError($"[OpenFleet] Resources/{ModulePrefabFolder}/{name} not found; run OpenFleet/Build Factory Scene.");
            }

            _modulePrefabs[kind] = prefab;
            return prefab;
        }

        /// <summary>
        /// 佈局重建後換上新的站點（物件名稱＝後端的站點代碼），並通知 <see cref="StationsChanged"/>。
        /// 舊的站點物件由呼叫端（LayoutScene）負責刪除。
        /// </summary>
        public void SetStations(IEnumerable<StationAnchor> anchors)
        {
            _stations.Clear();
            foreach (var anchor in anchors)
            {
                _stations[anchor.StationCode] = anchor;
                // 還沒收到後端狀態前先當作沒有料。
                anchor.ShowContents(false, false);
            }

            StationsChanged?.Invoke();
        }

        private void CreateDocument<T>(string objectName, PanelSettings panelSettings, int sortingOrder) where T : MonoBehaviour
        {
            // 先停用再加元件：UIDocument 啟用時就需要 PanelSettings。
            var go = new GameObject(objectName);
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            var document = go.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            document.sortingOrder = sortingOrder;
            go.AddComponent<T>();
            go.SetActive(true);
        }
    }
}
