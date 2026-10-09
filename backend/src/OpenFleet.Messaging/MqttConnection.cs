using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace OpenFleet.Messaging;

/// <summary>
/// 持有唯一一條 MQTT 連線的背景服務（Singleton）。
/// 負責：連線、斷線後重連並重新訂閱、把收到的訊息分派給 <see cref="IMqttMessageHandler"/>、提供發送功能。
/// MQTTnet 5 已移除 ManagedClient，因此重連迴圈在這裡自行實作。
/// </summary>
public sealed class MqttConnection : BackgroundService, IMqttPublisher
{
    private readonly IMqttClient _client;
    private readonly IReadOnlyList<IMqttMessageHandler> _handlers;
    private readonly ILogger<MqttConnection> _logger;
    private readonly MqttClientOptions _clientOptions;
    private CancellationToken _stoppingToken;

    public MqttConnection(IOptions<MqttSettings> settings, IEnumerable<IMqttMessageHandler> handlers, ILogger<MqttConnection> logger)
    {
        Settings = settings.Value;
        _handlers = handlers.ToList();
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(Settings.Host, Settings.Port)
            .WithClientId(string.IsNullOrEmpty(Settings.ClientId) ? $"openfleet-{Guid.NewGuid():N}" : Settings.ClientId)
            // CleanSession：每次重連都是新 session，由本類別重新訂閱。斷線期間的訊息不保證補收，需靠 HTTP 快照對齊。
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(10));

        if (!string.IsNullOrEmpty(Settings.Username))
        {
            builder.WithCredentials(Settings.Username, Settings.Password);
        }

        _clientOptions = builder.Build();
    }

    public MqttSettings Settings { get; }

    public bool IsConnected => _client.IsConnected;

    public Task PublishAsync<T>(string topic, T message, MqttQualityOfServiceLevel qos, bool retain, CancellationToken cancellationToken) =>
        PublishRawAsync(topic, JsonSerializer.SerializeToUtf8Bytes(message, JsonDefaults.Options), qos, retain, cancellationToken);

    public async Task PublishRawAsync(string topic, ReadOnlyMemory<byte> payload, MqttQualityOfServiceLevel qos, bool retain, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            throw new MqttNotConnectedException();
        }

        var applicationMessage = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(new ReadOnlySequence<byte>(payload))
            .WithContentType("application/json")
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retain)
            .Build();

        try
        {
            await _client.PublishAsync(applicationMessage, cancellationToken);
        }
        catch (MQTTnet.Exceptions.MqttCommunicationException) when (!_client.IsConnected)
        {
            throw new MqttNotConnectedException();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                await TryConnectAsync(stoppingToken);
            }

            try
            {
                await Task.Delay(Settings.ReconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken);
        }
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }

    private async Task TryConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.ConnectAsync(_clientOptions, cancellationToken);

            if (_handlers.Count > 0)
            {
                var subscribe = new MqttClientSubscribeOptionsBuilder();
                foreach (var handler in _handlers)
                {
                    subscribe.WithTopicFilter(handler.TopicFilter, handler.QualityOfService);
                }

                await _client.SubscribeAsync(subscribe.Build(), cancellationToken);
            }

            _logger.LogInformation("MQTT connected to {Host}:{Port}; subscribed {Count} filter(s).",
                Settings.Host, Settings.Port, _handlers.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MQTT connect to {Host}:{Port} failed: {Message}. Retrying in {Delay}.",
                Settings.Host, Settings.Port, ex.Message, Settings.ReconnectDelay);

            // 連上但訂閱失敗時主動斷開，下一輪會完整重來，避免「已連線卻沒訂閱」的半套狀態。
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
            }
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (e.ClientWasConnected && !_stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("MQTT disconnected ({Reason}); will reconnect.", e.Reason);
        }

        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var message = new MqttInboundMessage(topic, e.ApplicationMessage.Payload.ToArray());

        foreach (var handler in _handlers)
        {
            if (MqttTopicFilterComparer.Compare(topic, handler.TopicFilter) != MqttTopicFilterCompareResult.IsMatch)
            {
                continue;
            }

            try
            {
                await handler.HandleAsync(message, this, _stoppingToken);
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                // 單則壞訊息不能讓整條連線停擺；P1 只記錄。之後可加入 dead-letter 保存以便追查。
                _logger.LogError(ex, "Handler {Handler} failed for topic {Topic}.", handler.GetType().Name, topic);
            }
        }
    }
}
