using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Fleet;

/// <summary>車輛事件（接受／完成／失敗）：QoS 1，可能重送，因此先去重再更新狀態。</summary>
public sealed class VehicleEventHandler(
    IOptions<MqttSettings> mqtt,
    FleetStateStore store,
    InboxDeduplicator inbox,
    ILogger<VehicleEventHandler> logger) : IMqttMessageHandler
{
    public string TopicFilter { get; } = Topics.AllVehicles(mqtt.Value.FactoryId, VehicleChannel.Events);

    public MqttQualityOfServiceLevel QualityOfService => MqttQualityOfServiceLevel.AtLeastOnce;

    public Task HandleAsync(MqttInboundMessage message, IMqttPublisher _, CancellationToken cancellationToken)
    {
        if (!VehicleMessageParser.TryRead<VehicleEvent>(message, logger, out var vehicleId, out var envelope))
        {
            return Task.CompletedTask;
        }

        if (!inbox.TryRegister(envelope.ProducerId, envelope.MessageId))
        {
            logger.LogInformation("Duplicate event {MessageId} from {Producer} ignored.", envelope.MessageId, envelope.ProducerId);
            return Task.CompletedTask;
        }

        store.ApplyEvent(vehicleId, envelope.Payload);
        logger.LogInformation("Vehicle {VehicleId} reported {EventType} for command {CommandId}.",
            vehicleId, envelope.Payload.EventType, envelope.Payload.CommandId);
        return Task.CompletedTask;
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
