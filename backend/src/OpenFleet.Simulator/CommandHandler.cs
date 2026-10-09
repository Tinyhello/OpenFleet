using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Simulator;

/// <summary>接收後端指令並回報事件。P1 只支援 Ping：立即回報 Accepted 與 Completed。</summary>
public sealed class CommandHandler(
    IOptions<MqttSettings> mqtt,
    SimulatedFleet fleet,
    TimeProvider time,
    ILogger<CommandHandler> logger) : IMqttMessageHandler
{
    public string TopicFilter { get; } = Topics.AllVehicles(mqtt.Value.FactoryId, VehicleChannel.Commands);

    public MqttQualityOfServiceLevel QualityOfService => MqttQualityOfServiceLevel.AtLeastOnce;

    public async Task HandleAsync(MqttInboundMessage message, IMqttPublisher publisher, CancellationToken cancellationToken)
    {
        if (!Topics.TryParseVehicle(message.Topic, out _, out var vehicleId, out _) || !fleet.Contains(vehicleId))
        {
            return;
        }

        MessageEnvelope<VehicleCommand>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope<VehicleCommand>>(message.Payload.Span, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Malformed command on {Topic}: {Message}", message.Topic, ex.Message);
            return;
        }

        if (envelope?.Payload is not { } command || command.CommandId == Guid.Empty)
        {
            logger.LogWarning("Command on {Topic} is missing required fields.", message.Topic);
            return;
        }

        var replies = fleet.GetOrCreateReplies(command.CommandId, () => Execute(envelope, publisher.Settings.ProducerId), out var isDuplicate);
        if (isDuplicate)
        {
            logger.LogInformation("Duplicate command {CommandId} for {VehicleId}; re-sending previous replies.", command.CommandId, vehicleId);
        }

        var topic = Topics.Vehicle(publisher.Settings.FactoryId, vehicleId, VehicleChannel.Events);
        foreach (var reply in replies)
        {
            await publisher.PublishAsync(topic, reply, MqttQualityOfServiceLevel.AtLeastOnce, retain: false, cancellationToken);
        }
    }

    private IReadOnlyList<MessageEnvelope<VehicleEvent>> Execute(MessageEnvelope<VehicleCommand> envelope, string producerId)
    {
        var command = envelope.Payload;
        var now = time.GetUtcNow();

        MessageEnvelope<VehicleEvent> Reply(VehicleEventType type, string? reason = null) =>
            MessageEnvelope.Create(producerId, new VehicleEvent
            {
                CommandId = command.CommandId,
                AssignmentId = command.AssignmentId,
                EventType = type,
                Reason = reason,
            }, now, correlationId: envelope.MessageId);

        if (now > command.ExpiresAt)
        {
            return [Reply(VehicleEventType.Rejected, "Expired")];
        }

        return command.CommandType switch
        {
            VehicleCommandType.Ping => [Reply(VehicleEventType.Accepted), Reply(VehicleEventType.Completed)],
            _ => [Reply(VehicleEventType.Rejected, $"Unsupported command type {command.CommandType}")],
        };
    }
}
