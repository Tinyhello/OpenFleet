using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Simulator;

/// <summary>接收後端的放行，交給 <see cref="SimulatedFleet"/> 套用。重送或亂序的舊訊息由車輛端依版本忽略。</summary>
public sealed class ClearanceHandler(
    IOptions<MqttSettings> mqtt,
    SimulatedFleet fleet,
    ILogger<ClearanceHandler> logger) : IMqttMessageHandler
{
    public string TopicFilter { get; } = Topics.AllVehicles(mqtt.Value.FactoryId, VehicleChannel.Clearance);

    public MqttQualityOfServiceLevel QualityOfService => MqttQualityOfServiceLevel.AtLeastOnce;

    public Task HandleAsync(MqttInboundMessage message, IMqttPublisher publisher, CancellationToken cancellationToken)
    {
        if (!Topics.TryParseVehicle(message.Topic, out _, out var vehicleId, out _) || !fleet.Contains(vehicleId))
        {
            return Task.CompletedTask;
        }

        MessageEnvelope<VehicleClearance>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope<VehicleClearance>>(message.Payload.Span, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Malformed clearance on {Topic}: {Message}", message.Topic, ex.Message);
            return Task.CompletedTask;
        }

        if (envelope?.Payload is not { } clearance || clearance.PlanId == Guid.Empty || clearance.Path is null)
        {
            logger.LogWarning("Clearance on {Topic} is missing required fields.", message.Topic);
            return Task.CompletedTask;
        }

        fleet.ApplyClearance(vehicleId, clearance);
        return Task.CompletedTask;
    }
}
