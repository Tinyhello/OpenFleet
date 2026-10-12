using System.Collections;
using OpenFleet.Contracts;
using UnityEngine;
using UnityEngine.Networking;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 車隊顯示：每次連上 Broker 就取一次 HTTP 快照，之後以 MQTT 狀態更新；兩者都經過 Version 比對。
    /// 車輛物件由 PresentationController 依車型建立；這裡另提供簡易面板送出 Ping、讓攝影機跟隨車輛。
    /// </summary>
    [RequireComponent(typeof(FleetMqttClient), typeof(PresentationController))]
    public sealed class FleetView : MonoBehaviour
    {
        [SerializeField] private string apiBaseUrl = "http://localhost:5229";

        private PresentationController _presentation;
        private FleetMqttClient _mqtt;
        private string _status = "Connecting...";

        /// <summary>後端 HTTP API 的位址；其他面板共用這一個設定。</summary>
        public string ApiBaseUrl => apiBaseUrl;

        private void Awake()
        {
            _mqtt = GetComponent<FleetMqttClient>();
            _presentation = GetComponent<PresentationController>();
        }

        private void OnEnable()
        {
            _mqtt.VehicleStateReceived += Apply;
            _mqtt.Connected += OnConnected;
            _mqtt.Disconnected += OnDisconnected;
        }

        private void OnDisable()
        {
            _mqtt.VehicleStateReceived -= Apply;
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

            // 高度依行數計算，避免空白的背景擋住場景。
            var cameraController = _presentation.CameraController;
            var lines = _presentation.VehicleCount + (cameraController != null ? 2 : 1);
            GUILayout.BeginArea(new Rect(10, 10, 520, 12 + lines * 25), GUI.skin.box);
            GUILayout.Label(_status);
            foreach (var view in _presentation.Vehicles)
            {
                var s = view.State;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{s.VehicleId}  {s.VehicleType}  {s.Connectivity}  v{s.Version}  ping: {(s.LastCommandEvent?.ToString() ?? "-")}");
                if (GUILayout.Button("Ping", GUILayout.Width(50)))
                {
                    StartCoroutine(Ping(s.VehicleId));
                }

                if (cameraController != null)
                {
                    var following = cameraController.Following == view.transform;
                    if (GUILayout.Button(following ? "Unfollow" : "Follow", GUILayout.Width(70)))
                    {
                        cameraController.Follow(following ? null : view.transform);
                    }
                }

                GUILayout.EndHorizontal();
            }

            if (cameraController != null)
            {
                GUILayout.Label("Camera: right-drag rotate, middle-drag / WASD pan, wheel zoom, F reset");
            }

            GUILayout.EndArea();
        }
    }
}
