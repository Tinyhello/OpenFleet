using System.Collections;
using System.Collections.Generic;
using OpenFleet.Contracts;
using UnityEngine;
using UnityEngine.Networking;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 車隊顯示：每次連上 Broker 就取一次 HTTP 快照，之後以 MQTT 狀態更新；兩者都經過 Version 比對。
    /// 依後端回報的車型選用 AGV Prefab；另提供簡易面板送出 Ping、讓攝影機跟隨車輛。
    /// </summary>
    [RequireComponent(typeof(FleetMqttClient))]
    public sealed class FleetView : MonoBehaviour
    {
        [SerializeField] private string apiBaseUrl = "http://localhost:5229";
        [SerializeField] private Color onlineColor = new Color(0.2f, 0.8f, 0.3f);
        [SerializeField] private Color offlineColor = new Color(0.45f, 0.45f, 0.45f);

        [Header("AGV Prefabs")]
        [SerializeField] private VehicleView largeVehiclePrefab;
        [SerializeField] private VehicleView smallVehiclePrefab;
        [Tooltip("後端設定裡沒有車型的車輛。")]
        [SerializeField] private VehicleView unknownVehiclePrefab;

        [SerializeField] private FactoryCameraController cameraController;

        private readonly SortedDictionary<string, VehicleView> _vehicles = new SortedDictionary<string, VehicleView>();
        private FleetMqttClient _mqtt;
        private string _status = "Connecting...";

        private void Awake()
        {
            _mqtt = GetComponent<FleetMqttClient>();
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
            if (_vehicles.TryGetValue(state.VehicleId, out var view) && view.VehicleType != state.VehicleType && IsNewer(state, view.State))
            {
                // 車型設定改變（後端重啟後才會發生）：換成對應的模型。
                Debug.Log($"[OpenFleet] {state.VehicleId} type changed to {state.VehicleType}.");
                Destroy(view.gameObject);
                _vehicles.Remove(state.VehicleId);
                view = null;
            }

            if (view == null)
            {
                view = VehicleView.Create(PrefabFor(state.VehicleType), state.VehicleId, transform);
                _vehicles.Add(state.VehicleId, view);
            }

            var previous = view.State;
            if (!view.Apply(state, onlineColor, offlineColor))
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

        private VehicleView PrefabFor(VehicleType type)
        {
            switch (type)
            {
                case VehicleType.Large: return largeVehiclePrefab;
                case VehicleType.Small: return smallVehiclePrefab;
                default: return unknownVehiclePrefab;
            }
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
            // 高度依行數計算，避免空白的背景擋住場景。
            var lines = _vehicles.Count + (cameraController != null ? 2 : 1);
            GUILayout.BeginArea(new Rect(10, 10, 520, 12 + lines * 25), GUI.skin.box);
            GUILayout.Label(_status);
            foreach (var pair in _vehicles)
            {
                var view = pair.Value;
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
