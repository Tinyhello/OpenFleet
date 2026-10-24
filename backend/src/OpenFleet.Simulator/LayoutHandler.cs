using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Simulator;

/// <summary>
/// 接收後端發布的佈局版本（retained），交給 <see cref="SimulatedFleet"/> 套用：車輛清單與車位都跟著佈局走。
/// 沒有佈局時模擬器沒有車，遙測迴圈照常執行。
/// </summary>
public sealed class LayoutHandler(
    IOptions<MqttSettings> mqtt,
    SimulatedFleet fleet,
    ILogger<LayoutHandler> logger) : IMqttMessageHandler
{
    public string TopicFilter { get; } = Topics.Layout(mqtt.Value.FactoryId);

    public MqttQualityOfServiceLevel QualityOfService => MqttQualityOfServiceLevel.AtLeastOnce;

    public Task HandleAsync(MqttInboundMessage message, IMqttPublisher publisher, CancellationToken cancellationToken)
    {
        MessageEnvelope<LayoutRevisionMessage>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope<LayoutRevisionMessage>>(message.Payload.Span, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Malformed layout on {Topic}: {Message}", message.Topic, ex.Message);
            return Task.CompletedTask;
        }

        if (envelope?.Payload is not { } layout)
        {
            logger.LogWarning("Layout on {Topic} is missing its payload.", message.Topic);
            return Task.CompletedTask;
        }

        fleet.ApplyLayout(layout);
        logger.LogInformation("Layout {Name} applied: {Count} vehicle(s).", layout.Name, layout.Vehicles.Count);
        return Task.CompletedTask;
    }
}
