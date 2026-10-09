using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Api.Transport;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Fleet;

/// <summary>
/// 車輛事件（接受／取料／完成／失敗）：QoS 1，可能重送。去重與業務變更在同一筆資料庫交易完成，
/// 提交之後才更新記憶體中的顯示狀態。
/// 這個處理者是 Singleton，DbContext 是 Scoped，所以每則訊息建立一個 scope。
/// </summary>
public sealed class VehicleEventHandler(
    IOptions<MqttSettings> mqtt,
    FleetStateStore store,
    IServiceScopeFactory scopes,
    ILogger<VehicleEventHandler> logger) : IMqttMessageHandler
{
    private const int MaxAttempts = 3;

    public string TopicFilter { get; } = Topics.AllVehicles(mqtt.Value.FactoryId, VehicleChannel.Events);

    public MqttQualityOfServiceLevel QualityOfService => MqttQualityOfServiceLevel.AtLeastOnce;

    public async Task HandleAsync(MqttInboundMessage message, IMqttPublisher _, CancellationToken cancellationToken)
    {
        if (!VehicleMessageParser.TryRead<VehicleEvent>(message, logger, out var vehicleId, out var envelope))
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<VehicleEventProcessor>();
                if (!await processor.ProcessAsync(vehicleId, envelope, cancellationToken))
                {
                    logger.LogInformation("Duplicate event {MessageId} from {Producer} ignored.", envelope.MessageId, envelope.ProducerId);
                    return;
                }

                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // 樂觀並行衝突：派工迴圈剛好改了同一筆派工（例如標記逾時）。整筆交易已回滾，用新的 scope 重讀再做一次。
                logger.LogInformation("Concurrent update while handling event {MessageId}; retrying ({Attempt}/{Max}).", envelope.MessageId, attempt, MaxAttempts);
            }
        }

        store.ApplyEvent(vehicleId, envelope.Payload);
        logger.LogInformation("Vehicle {VehicleId} reported {EventType} for command {CommandId}.",
            vehicleId, envelope.Payload.EventType, envelope.Payload.CommandId);
    }
}

/// <summary>位置遙測：QoS 0，允許遺失；以 SessionId + Sequence 丟棄亂序與重複。</summary>
public sealed class VehicleTelemetryHandler(
    IOptions<MqttSettings> mqtt,
    FleetStateStore store,
    ILogger<VehicleTelemetryHandler> logger) : IMqttMessageHandler
{
    public string TopicFilter { get; } = Topics.AllVehicles(mqtt.Value.FactoryId, VehicleChannel.Telemetry);

    public MqttQualityOfServiceLevel QualityOfService => MqttQualityOfServiceLevel.AtMostOnce;

    public Task HandleAsync(MqttInboundMessage message, IMqttPublisher _, CancellationToken cancellationToken)
    {
        if (VehicleMessageParser.TryRead<VehicleTelemetry>(message, logger, out var vehicleId, out var envelope))
        {
            store.ApplyTelemetry(vehicleId, envelope.Payload);
        }

        return Task.CompletedTask;
    }
}

internal static class VehicleMessageParser
{
    public static bool TryRead<T>(MqttInboundMessage message, ILogger logger, out string vehicleId, out MessageEnvelope<T> envelope)
        where T : class
    {
        envelope = null!;

        if (!Topics.TryParseVehicle(message.Topic, out _, out vehicleId, out _))
        {
            logger.LogWarning("Unrecognised topic {Topic}.", message.Topic);
            return false;
        }

        MessageEnvelope<T>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MessageEnvelope<T>>(message.Payload.Span, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Malformed JSON on {Topic}: {Message}", message.Topic, ex.Message);
            return false;
        }

        if (parsed?.Payload is null || parsed.MessageId == Guid.Empty || string.IsNullOrEmpty(parsed.ProducerId))
        {
            logger.LogWarning("Envelope on {Topic} is missing required fields.", message.Topic);
            return false;
        }

        if (parsed.SchemaVersion != ContractVersion.Current)
        {
            logger.LogWarning("Unsupported schemaVersion {Version} on {Topic}.", parsed.SchemaVersion, message.Topic);
            return false;
        }

        envelope = parsed;
        return true;
    }
}
