using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using OpenFleet.Contracts;
using UnityEngine;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 訂閱後端發布的車輛狀態與使用中的佈局版本。MQTTnet 在背景執行緒收訊，
    /// 這裡只做解析，再透過佇列交給主執行緒（Update）發出事件，事件處理者可以直接操作場景。
    /// 斷線重連與重新訂閱由 ManagedMqttClient 負責。
    /// </summary>
    public sealed class FleetMqttClient : MonoBehaviour
    {
        [SerializeField] private string host = "localhost";
        [SerializeField] private int port = 1883;
        [SerializeField] private string factoryId = "demo";
        [SerializeField] private float reconnectDelaySeconds = 2f;

        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private IManagedMqttClient _client;

        /// <summary>主執行緒呼叫。</summary>
        public event Action<VehicleState> VehicleStateReceived;

        /// <summary>
        /// 主執行緒呼叫。車輛的 state Topic 被清掉（空的 retained 訊息）：這台車已經不在佈局裡，畫面上要移除它。
        /// </summary>
        public event Action<string> VehicleStateCleared;

        /// <summary>
        /// 主執行緒呼叫。使用中的佈局版本（retained）：連上 Broker 時會收到目前的版本，之後每次套用佈局都會再收到一次。
        /// </summary>
        public event Action<LayoutRevisionMessage> LayoutRevisionReceived;

        /// <summary>主執行緒呼叫。每次（重新）連上 Broker 都會觸發，可用來重新取 HTTP 快照。</summary>
        public event Action Connected;

        /// <summary>主執行緒呼叫。</summary>
        public event Action Disconnected;

        public bool IsConnected { get; private set; }

        public string FactoryId => factoryId;

        private async void Start()
        {
            var client = new MqttFactory().CreateManagedMqttClient();
            client.ApplicationMessageReceivedAsync += OnMessageReceived;
            client.ConnectedAsync += _ =>
            {
                _mainThread.Enqueue(() =>
                {
                    IsConnected = true;
                    Connected?.Invoke();
                });
                return Task.CompletedTask;
            };
            client.DisconnectedAsync += _ =>
            {
                // 每次重連失敗也會觸發，只在「原本是連線中」時通知一次。
                _mainThread.Enqueue(() =>
                {
                    if (!IsConnected)
                    {
                        return;
                    }

                    IsConnected = false;
                    Disconnected?.Invoke();
                });
                return Task.CompletedTask;
            };
            _client = client;

            var options = new ManagedMqttClientOptionsBuilder()
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(reconnectDelaySeconds))
                .WithClientOptions(new MqttClientOptionsBuilder()
                    .WithTcpServer(host, port)
                    // 多開 Editor／Player 時 ClientId 不能重複，否則 Broker 會把前一條連線踢掉。
                    .WithClientId($"openfleet-unity-{Guid.NewGuid():N}")
                    .WithCleanSession()
                    .Build())
                .Build();

            try
            {
                // ManagedMqttClient 會記住訂閱，每次重連後自動重新訂閱；state Topic 是 retained，訂閱後立刻收到最新值。
                await client.SubscribeAsync(Topics.AllVehicleStates(factoryId), MqttQualityOfServiceLevel.AtLeastOnce);
                await client.SubscribeAsync(Topics.Layout(factoryId), MqttQualityOfServiceLevel.AtLeastOnce);
                await client.StartAsync(options);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
            }
        }

        private void Update()
        {
            while (_mainThread.TryDequeue(out var action))
            {
                action();
            }
        }

        private void OnDestroy()
        {
            var client = _client;
            _client = null;
            if (client != null)
            {
                _ = StopAsync(client);
            }
        }

        private static async Task StopAsync(IManagedMqttClient client)
        {
            try
            {
                await client.StopAsync();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                client.Dispose();
            }
        }

        // 背景執行緒：不可碰 UnityEngine.Object。
        private Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            var topic = e.ApplicationMessage.Topic;
            var segment = e.ApplicationMessage.PayloadSegment;
            var isLayout = topic == Topics.Layout(factoryId);
            if (segment.Count == 0)
            {
                // 空 payload 代表 retained 訊息被清除。車輛的 state 被清掉表示這台車已經不在佈局裡（套用佈局時後端清的）；
                // 佈局 Topic 不會被清掉，真的收到就忽略。
                if (!isLayout && TryParseStateVehicle(topic, out var vehicleId))
                {
                    _mainThread.Enqueue(() => VehicleStateCleared?.Invoke(vehicleId));
                }

                return Task.CompletedTask;
            }

            if (isLayout)
            {
                var revision = Parse<LayoutRevisionMessage>(topic, segment);
                if (revision != null)
                {
                    _mainThread.Enqueue(() => LayoutRevisionReceived?.Invoke(revision));
                }

                return Task.CompletedTask;
            }

            var state = Parse<VehicleState>(topic, segment);
            if (state != null)
            {
                _mainThread.Enqueue(() => VehicleStateReceived?.Invoke(state));
            }

            return Task.CompletedTask;
        }

        /// <summary>解析 MessageEnvelope 並檢查契約版本；格式不對或版本不符時記錄警告並回傳 null。</summary>
        private static T Parse<T>(string topic, ArraySegment<byte> segment) where T : class
        {
            MessageEnvelope<T> envelope;
            try
            {
                var json = Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count);
                envelope = FleetJson.Deserialize<MessageEnvelope<T>>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpenFleet] Invalid message on {topic}: {ex.Message}");
                return null;
            }

            if (envelope?.Payload == null || envelope.SchemaVersion != ContractVersion.Current)
            {
                Debug.LogWarning($"[OpenFleet] Ignored message on {topic} (schemaVersion {envelope?.SchemaVersion}).");
                return null;
            }

            return envelope.Payload;
        }

        /// <summary>從車輛 state Topic（<c>.../state/vehicles/{vehicleId}</c>）取出 vehicleId。</summary>
        private bool TryParseStateVehicle(string topic, out string vehicleId)
        {
            var prefix = Topics.VehicleState(factoryId, string.Empty);
            if (topic.StartsWith(prefix, StringComparison.Ordinal) && topic.Length > prefix.Length && topic.IndexOf('/', prefix.Length) < 0)
            {
                vehicleId = topic.Substring(prefix.Length);
                return true;
            }

            vehicleId = null;
            return false;
        }
    }
}
