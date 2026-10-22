using System.Collections.Generic;
using OpenFleet.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 表演用的控制器：畫面上的物件與顏色都由這裡管理，其他腳本不在 Inspector 掛參考。
    /// 1. 物件：AGV Prefab 與 UI 設定從 Resources 載入；場景裡的站點、攝影機用名稱或型別找到；操作面板與進度條在執行時建立。
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

        /// <summary>場景中放站點的物件路徑；底下每個子物件的名稱就是後端的站點代碼。</summary>
        public const string StationsPath = "Factory/Stations";

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

            /// <summary>急單（排在所有訂單前面）的標示。</summary>
            public static readonly Color Rush = new Color(1f, 0.62f, 0.3f);
            public static readonly Color InputText = new Color(0.08f, 0.09f, 0.11f);
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

        /// <summary>執行時用的 PanelSettings 複本：改縮放時不會改到專案裡的資產。</summary>
        private PanelSettings _panelSettings;
        private int _uiSizeStep = DefaultUiSizeStep;
        private int _screenHeight;

        public static PresentationController Instance { get; private set; }

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

        /// <summary>場景中的站點，以後端的站點代碼為鍵。</summary>
        public IReadOnlyDictionary<string, StationAnchor> Stations => _stations;

        /// <summary>
        /// 以 <c>-hide-panels</c> 啟動時不顯示車輛面板與操作面板（錄影、截圖用）；機台與車輛上的進度條照常顯示。
        /// </summary>
        public static bool PanelsHidden { get; } = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-hide-panels") >= 0;

        private void Awake()
        {
            Instance = this;
            Fleet = GetComponent<FleetView>();
            CameraController = FindAnyObjectByType<FactoryCameraController>();

            BindStations();

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

        /// <summary>依車型從 Resources 載入 Prefab 並建立一台車。</summary>
        public VehicleView SpawnVehicle(string vehicleId, VehicleType type)
        {
            var view = VehicleView.Create(PrefabFor(type), vehicleId, type, transform);
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

        private void BindStations()
        {
            var root = GameObject.Find(StationsPath);
            if (root == null)
            {
                Debug.LogError($"[OpenFleet] Scene object '{StationsPath}' not found; station overlays are disabled.");
                return;
            }

            foreach (Transform station in root.transform)
            {
                var anchor = station.gameObject.AddComponent<StationAnchor>();
                _stations[anchor.StationCode] = anchor;
                // 還沒收到後端狀態前先當作沒有料。
                anchor.ShowContents(false, false);
            }
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
