using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Simulator;

/// <summary>
/// 接收後端指令並交給 <see cref="SimulatedFleet"/> 執行。Ping 立即回報；
/// Transport／MoveTo 先回報 Accepted，之後的 Loaded／Completed 由模擬迴圈在車輛實際到達時產生。
/// </summary>
public sealed class CommandHandler(
    IOptions<MqttSettings> mqtt,
    SimulatedFleet fleet,
    EventSender sender,
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

        if (fleet.HandleCommand(vehicleId, envelope))
        {
            logger.LogInformation("Duplicate command {CommandId} for {VehicleId}; re-sending previous replies.", command.CommandId, vehicleId);
        }
        else
        {
            logger.LogInformation("Command {CommandType} {CommandId} received for {VehicleId}.", command.CommandType, command.CommandId, vehicleId);
        }

        await sender.FlushAsync(publisher, cancellationToken);
    }
}
