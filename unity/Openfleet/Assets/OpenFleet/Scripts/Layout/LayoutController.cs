using System;
using System.Collections;
using System.Collections.Generic;
using OpenFleet.Contracts;
using OpenFleet.Layout;
using UnityEngine;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 向後端要使用中的佈局、依它建場景，並在佈局版本改變時自動重建。
    /// 掛在 OpenFleet 物件上（FleetView 的 RequireComponent 會自動加上，和 FleetMqttClient、PresentationController 一樣不必在場景裡設定）。
    ///
    /// 1. 啟動時 GET /api/layouts/active；後端還沒起來就每 <see cref="RetrySeconds"/> 秒重試，左上的狀態列顯示「等待後端佈局…」。
    /// 2. 收到 MQTT 的 <see cref="LayoutRevisionMessage"/>：版本（佈局 ID 與套用時間）和目前建好的不同就重新要佈局、重建。
    ///    編輯模式中（<see cref="EditMode"/>）先記著，離開編輯時再重建。
    /// 3. 重建後：把新的站點交給 PresentationController（觸發 StationsChanged，進度條跟著重建）、
    ///    移除不在車輛清單裡的車、更新攝影機的預設視角。
    /// </summary>
    public sealed class LayoutController : MonoBehaviour
    {
        public const float RetrySeconds = 3f;

        /// <summary>狀態列在還沒有佈局時顯示的文字。</summary>
        public const string WaitingText = "等待後端佈局…";

        private PresentationController _presentation;
        private FleetView _fleet;
        private FleetMqttClient _mqtt;
        private LayoutClient _client;
        private LayoutScene _scene;
        private LayoutDto _active;
        private LayoutRevisionMessage _revision;
        private readonly HashSet<string> _vehicleIds = new HashSet<string>(StringComparer.Ordinal);
        private bool _needsLoad = true;
        private bool _editMode;
        private string _homeFloorKey;

        /// <summary>目前建好的佈局；還沒收到時是 null。</summary>
        public LayoutDto Active => _active;

        /// <summary>目前建好的場景。</summary>
        public LayoutScene Scene => _scene;

        public bool HasLayout => _active != null;

        /// <summary>最近一次 MQTT 收到的佈局版本（含車輛清單）；還沒收到時是 null。</summary>
        public LayoutRevisionMessage Revision => _revision;

        /// <summary>
        /// 這台車是不是目前佈局的車。還沒收到佈局版本時一律算是（車輛狀態可能比版本先到）。
        /// FleetView 用它丟掉已經被拿掉的車的狀態：後端清掉 state Topic 之後，偶爾還會再多送一則舊的 retained 狀態，不丟掉的話車會又冒出來。
        /// </summary>
        public bool IsVehicleInLayout(string vehicleId) => _revision == null || _vehicleIds.Contains(vehicleId);

        /// <summary>左上狀態列要顯示的佈局狀態；已經有佈局時是空字串。</summary>
        public string StatusText => HasLayout ? string.Empty : WaitingText;

        /// <summary>佈局的 HTTP 呼叫；編輯與套用用同一個。</summary>
        public LayoutClient Client => _client;

        /// <summary>每次場景（重新）建好之後發出，第一次也算。事件發出時 PresentationController 的站點已經換成新的。</summary>
        public event Action LayoutBuilt;

        /// <summary>
        /// 編輯模式中場景由編輯器自己畫（草稿），收到新版本只記著，離開編輯時才重建。
        /// </summary>
        public bool EditMode
        {
            get => _editMode;
            set => _editMode = value;
        }

        private void Awake()
        {
            _presentation = GetComponent<PresentationController>();
            _fleet = GetComponent<FleetView>();
            _mqtt = GetComponent<FleetMqttClient>();
            _client = new LayoutClient(() => _fleet.ApiBaseUrl);
        }

        private void OnEnable()
        {
            _mqtt.LayoutRevisionReceived += OnRevision;
            StartCoroutine(LoadLoop());
        }

        private void OnDisable()
        {
            _mqtt.LayoutRevisionReceived -= OnRevision;
            StopAllCoroutines();
        }

        private void OnDestroy()
        {
            _scene?.Clear();
        }

        /// <summary>
        /// 離開編輯模式（LayoutEditor 呼叫）：場景回到使用中的佈局。編輯中收到新版本（<c>_needsLoad</c>）時不用先畫舊的，
        /// 載入迴圈下一個 frame 就會向後端重新要一份並重建。
        /// </summary>
        public void LeaveEditMode()
        {
            _editMode = false;
            if (_active != null && !_needsLoad)
            {
                Rebuild(_active);
            }
        }

        /// <summary>
        /// 通知「佈局可能變了」，下一個 frame 重新向後端要使用中的佈局並重建（第 4 步套用佈局之後用）。
        /// </summary>
        public void Reload()
        {
            _needsLoad = true;
        }

        /// <summary>
        /// 啟動後一直跑：要重載時向後端要佈局，失敗就每 3 秒重試；成功就重建場景，然後等下一次要重載。
        /// 載入的過程中又收到新的版本時，<c>_needsLoad</c> 會再被設成 true，所以這一輪結束後立刻再載一次。
        /// </summary>
        private IEnumerator LoadLoop()
        {
            while (true)
            {
                if (!_needsLoad || _editMode)
                {
                    yield return null;
                    continue;
                }

                _needsLoad = false;
                LayoutResponse<LayoutDto> response = null;
                yield return _client.GetActive(r => response = r);

                if (!response.Ok || response.Value == null || response.Value.Document == null)
                {
                    // 後端還沒起來、還沒有使用中的佈局：保持「等待」，稍後重試。
                    _needsLoad = true;
                    yield return new WaitForSecondsRealtime(RetrySeconds);
                    continue;
                }

                Rebuild(response.Value);
            }
        }

        private void OnRevision(LayoutRevisionMessage message)
        {
            _revision = message;
            _vehicleIds.Clear();
            foreach (var vehicle in message.Vehicles)
            {
                _vehicleIds.Add(vehicle.VehicleId);
            }

            PruneVehicles();
            if (!IsCurrent(message))
            {
                _needsLoad = true;
            }
        }

        /// <summary>訊息的版本和目前建好的佈局是不是同一版（佈局 ID 與套用時間都相同）。</summary>
        private bool IsCurrent(LayoutRevisionMessage message)
        {
            if (_active == null || _active.Id != message.LayoutId)
            {
                return false;
            }

            // 套用時間在資料庫與 JSON 之間可能差一點精度，容許 1 毫秒。
            return !_active.AppliedAt.HasValue || Math.Abs((_active.AppliedAt.Value - message.AppliedAt).TotalMilliseconds) < 1;
        }

        private void Rebuild(LayoutDto layout)
        {
            var materials = _presentation.LayoutMaterials;
            if (materials == null)
            {
                // 資源缺漏已經記錄；不重試，重試也不會變好。
                enabled = false;
                return;
            }

            if (_scene == null)
            {
                _scene = new LayoutScene(materials, _presentation.ModulePrefab);
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            _scene.Build(layout.Document, false);
            _active = layout;

            _presentation.SetStations(_scene.Anchors);
            PruneVehicles();

            ApplyHome(layout.Document.Floor);

            // 版本訊息比 HTTP 回應先到時，上一次記下的「需要重載」已經沒有意義（這次載到的就是那一版）。
            if (_revision != null && IsCurrent(_revision))
            {
                _needsLoad = false;
            }

            Debug.Log($"[OpenFleet] Layout built: {layout.Name} ({layout.Document.Modules.Count} modules, {layout.Document.RoadNodes.Count} road nodes) in {watch.ElapsedMilliseconds} ms.");
            LayoutBuilt?.Invoke();
        }

        /// <summary>
        /// 預設視角（使用者 2026-10-25 決定）：整個廠房（外牆外框）都在沒被右邊操作面板蓋住的畫面裡，
        /// 目標與距離由攝影機依視野與面板寬度算（FactoryCameraController.FitHome），面板收起或視窗大小改變時也會重算。
        /// 廠房外框沒變時（例如只是換了機台位置）只更新預設視角，不動使用者目前的視角；第一次與外框改變時攝影機一起回到預設視角。
        /// </summary>
        private void ApplyHome(LayoutFloor floor)
        {
            var camera = _presentation.CameraController;
            if (camera == null)
            {
                return;
            }

            // 形狀可能是任意多邊形（P7 第 7 步），視角看外牆頂點的外框；舊文件的矩形也走同一條路。
            var bounds = LayoutFloorShape.Bounds(floor);
            var minX = (float)bounds.MinX;
            var maxX = (float)bounds.MaxX;
            var minZ = (float)bounds.MinZ;
            var maxZ = (float)bounds.MaxZ;
            var key = $"{minX}|{maxX}|{minZ}|{maxZ}";
            camera.FitHome(minX, maxX, minZ, maxZ, moveCamera: key != _homeFloorKey);
            _homeFloorKey = key;
        }

        /// <summary>移除不在車輛清單裡的車（套用佈局時被拿掉車位的車）；還沒收到版本時沒有清單，什麼都不移除。</summary>
        private void PruneVehicles()
        {
            if (_revision == null)
            {
                return;
            }

            var remove = new List<string>();
            foreach (var view in _presentation.Vehicles)
            {
                if (!_vehicleIds.Contains(view.name))
                {
                    remove.Add(view.name);
                }
            }

            foreach (var id in remove)
            {
                Debug.Log($"[OpenFleet] {id} is no longer in the layout; removed.");
                _presentation.RemoveVehicle(id);
            }
        }
    }
}
