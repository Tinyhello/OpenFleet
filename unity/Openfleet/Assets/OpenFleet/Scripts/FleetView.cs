using System.Collections;
using OpenFleet.Contracts;
using UnityEngine;
using UnityEngine.Networking;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 車隊顯示：每次連上 Broker 就取一次 HTTP 快照，之後以 MQTT 狀態更新；兩者都經過 Version 比對。
    /// 車輛物件由 PresentationController 依車型建立；這裡另提供簡易面板送出 Ping、讓攝影機跟隨車輛。
    /// 套用佈局時被拿掉車位的車，state Topic 會被後端清掉（空的 retained 訊息），這裡收到就把那台車移除。
    /// </summary>
    [RequireComponent(typeof(FleetMqttClient), typeof(PresentationController), typeof(LayoutController))]
    [RequireComponent(typeof(LayoutEditor))]
    public sealed class FleetView : MonoBehaviour
    {
        [SerializeField] private string apiBaseUrl = "http://localhost:5229";

        private PresentationController _presentation;
        private FleetMqttClient _mqtt;
        private LayoutController _layout;
        private string _status = "Connecting...";

        /// <summary>後端 HTTP API 的位址；其他面板共用這一個設定。</summary>
        public string ApiBaseUrl => apiBaseUrl;

        /// <summary>
        /// 左上狀態框在螢幕上的範圍（原點在左上，像素；最近一次繪製的結果，沒畫時是空的）。
        /// 佈局編輯用它判斷滑鼠是不是在這個框上，不然點框會被當成點場景。
        /// </summary>
        public static Rect OverlayRect { get; private set; }

        private void Awake()
        {
            _mqtt = GetComponent<FleetMqttClient>();
            _presentation = GetComponent<PresentationController>();
            _layout = GetComponent<LayoutController>();
        }

        private void OnEnable()
        {
            _mqtt.VehicleStateReceived += Apply;
            _mqtt.VehicleStateCleared += _presentation.RemoveVehicle;
            _layout.LayoutBuilt += OnLayoutBuilt;
            _mqtt.Connected += OnConnected;
            _mqtt.Disconnected += OnDisconnected;
        }

        private void OnDisable()
        {
            _mqtt.VehicleStateReceived -= Apply;
            _mqtt.VehicleStateCleared -= _presentation.RemoveVehicle;
            _layout.LayoutBuilt -= OnLayoutBuilt;
            _mqtt.Connected -= OnConnected;
            _mqtt.Disconnected -= OnDisconnected;
        }

        private void OnConnected()
        {
            _status = "MQTT connected";
            Debug.Log("[OpenFleet] MQTT connected.");
            // 先訂閱再取快照：兩者重疊的部分由 Version 比對去掉，不會漏掉中間的更新。
            StartCoroutine(LoadSnapshot());
        }

        /// <summary>
        /// 佈局建好（第一次、或換了版本）：重新取一次車輛快照。第一次是因為佈局建好之前收到的狀態都被丟掉了；
        /// 之後是讓新加入的車馬上出現，不必等它的下一則狀態。
        /// </summary>
        private void OnLayoutBuilt()
        {
            StartCoroutine(LoadSnapshot());
        }

        private void OnDisconnected()
        {
            _status = "MQTT disconnected, reconnecting...";
            Debug.Log("[OpenFleet] MQTT disconnected.");
        }

        private IEnumerator LoadSnapshot()
        {
            using (var request = UnityWebRequest.Get($"{apiBaseUrl}/api/vehicles"))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[OpenFleet] Snapshot failed: {request.error}");
                    yield break;
                }

                var states = FleetJson.Deserialize<VehicleState[]>(request.downloadHandler.text);
                foreach (var state in states)
                {
                    Apply(state);
                }

                Debug.Log($"[OpenFleet] Snapshot loaded: {states.Length} vehicle(s).");
            }
        }

        private void Apply(VehicleState state)
        {
            // 佈局還沒建好時不顯示車輛（後端沒開時 Broker 上留著舊的 retained 狀態，車會浮在空地上）；
            // 建好之後會重新取一次快照。已經不在佈局裡的車（套用佈局時被拿掉車位）也不理：
            // 後端偶爾會在清掉 state Topic 之後多送一則舊狀態，不丟掉的話車會又冒出來。
            if (_layout != null && (!_layout.HasLayout || !_layout.IsVehicleInLayout(state.VehicleId)))
            {
                return;
            }

            if (_presentation.TryGetVehicle(state.VehicleId, out var view) && view.VehicleType != state.VehicleType && IsNewer(state, view.State))
            {
                // 車型設定改變（後端重啟後才會發生）：換成對應的模型。
                Debug.Log($"[OpenFleet] {state.VehicleId} type changed to {state.VehicleType}.");
                _presentation.RemoveVehicle(state.VehicleId);
                view = null;
            }

            if (view == null)
            {
                view = _presentation.SpawnVehicle(state.VehicleId, state.VehicleType);
            }

            var previous = view.State;
            if (!view.Apply(state))
            {
                return;
            }

            if (previous != null && previous.StateEpoch != state.StateEpoch)
            {
                Debug.Log($"[OpenFleet] {state.VehicleId} state epoch changed (backend restarted), now v{state.Version}.");
            }

            if (previous == null || previous.Connectivity != state.Connectivity)
            {
                Debug.Log($"[OpenFleet] {state.VehicleId} is {state.Connectivity} (v{state.Version}).");
            }

            if (state.LastCommandId != null && (previous?.LastCommandId != state.LastCommandId || previous.LastCommandEvent != state.LastCommandEvent))
            {
                Debug.Log($"[OpenFleet] {state.VehicleId} command {state.LastCommandId}: {state.LastCommandEvent}.");
            }
        }

        private static bool IsNewer(VehicleState state, VehicleState current)
        {
            return current == null || state.StateEpoch != current.StateEpoch || state.Version > current.Version;
        }

        private IEnumerator Ping(string vehicleId)
        {
            using (var request = new UnityWebRequest($"{apiBaseUrl}/api/vehicles/{UnityWebRequest.EscapeURL(vehicleId)}/ping", UnityWebRequest.kHttpVerbPOST))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[OpenFleet] Ping {vehicleId} failed: {request.responseCode} {request.error}");
                }
            }
        }

        // 驗證用的簡易面板；正式 UI（任務清單、車輛詳情）之後再做。
        private void OnGUI()
        {
            if (PresentationController.PanelsHidden)
            {
                return;
            }

            // 和操作面板一樣依介面縮放放大：字型與尺寸各自乘上倍數（不用 GUI.matrix，放大後字才不會糊）。
            var scale = PresentationController.UiScale;
            EnsureStyles(scale);

            // 背景框跟著內容的高度（框是區域裡的垂直群組，不是整個區域），不會多出空白擋住場景。
            var cameraController = _presentation.CameraController;
            GUILayout.BeginArea(new Rect(10, 10, 520 * scale, Screen.height - 20));
            GUILayout.BeginVertical(GUI.skin.box);

            // 還沒有佈局時（後端沒開或還沒有使用中的佈局）在連線狀態後面接上「等待後端佈局…」。
            var layoutStatus = _layout != null ? _layout.StatusText : string.Empty;
            GUILayout.Label(layoutStatus.Length > 0 ? $"{_status}　{layoutStatus}" : _status, _labelStyle);
            // 佈局編輯中車輛隱藏，跟隨與 Ping 都沒有意義，列表先收起來。
            foreach (var view in _presentation.LayoutEditing ? System.Linq.Enumerable.Empty<VehicleView>() : _presentation.Vehicles)
            {
                var s = view.State;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{s.VehicleId}  {s.VehicleType}  {s.Connectivity}  v{s.Version}  ping: {(s.LastCommandEvent?.ToString() ?? "-")}", _labelStyle);
                if (GUILayout.Button("Ping", _buttonStyle, GUILayout.Width(50 * scale)))
                {
                    StartCoroutine(Ping(s.VehicleId));
                }

                if (cameraController != null)
                {
                    var following = cameraController.Following == view.transform;
                    if (GUILayout.Button(following ? "Unfollow" : "Follow", _buttonStyle, GUILayout.Width(70 * scale)))
                    {
                        cameraController.Follow(following ? null : view.transform);
                    }
                }

                GUILayout.EndHorizontal();
            }

            if (cameraController != null)
            {
                GUILayout.Label("Camera: right-drag rotate, middle-drag / WASD pan, wheel zoom, F reset", _labelStyle);
            }

            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
            {
                // 結束群組後的 LastRect 就是整個狀態框（區域內座標，加上區域的左上角 10, 10）。
                var boxRect = GUILayoutUtility.GetLastRect();
                OverlayRect = new Rect(boxRect.x + 10, boxRect.y + 10, boxRect.width, boxRect.height);
            }

            GUILayout.EndArea();
        }

        private GUIStyle _labelStyle;
        private GUIStyle _buttonStyle;
        private float _styleScale;

        /// <summary>預設的 IMGUI 字型約 12 像素；依介面縮放建立放大後的樣式，倍數變了才重建。</summary>
        private void EnsureStyles(float scale)
        {
            if (_labelStyle != null && Mathf.Approximately(_styleScale, scale))
            {
                return;
            }

            _styleScale = scale;
            var size = Mathf.RoundToInt(12 * scale);
            // 預設的 IMGUI 字型沒有中文字（「等待後端佈局…」會被畫成像被劃掉的方框），改用和操作面板相同的作業系統中文字型；
            // 找不到字型時 font 是 null，維持預設字型。英文在這個字型下也正常。
            var font = ProductionPanel.CjkFont();
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = size, font = font };
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = size, font = font };
        }
    }
}
