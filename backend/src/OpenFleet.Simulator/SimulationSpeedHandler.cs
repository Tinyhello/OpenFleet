using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Simulator;

/// <summary>
/// 接收後端發布的模擬時間倍率（retained，所以模擬器比後端晚啟動也拿得到目前的值）。
/// </summary>
public sealed class SimulationSpeedHandler(
    IOptions<MqttSettings> mqtt,
    SimulatedFleet fleet,
    ILogger<SimulationSpeedHandler> logger) : IMqttMessageHandler
{
    private const double MaxTimeScale = 20;

    public string TopicFilter { get; } = Topics.SimulationState(mqtt.Value.FactoryId);

    public MqttQualityOfServiceLevel QualityOfService => MqttQualityOfServiceLevel.AtLeastOnce;

    public Task HandleAsync(MqttInboundMessage message, IMqttPublisher publisher, CancellationToken cancellationToken)
    {
        MessageEnvelope<SimulationSettingsDto>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope<SimulationSettingsDto>>(message.Payload.Span, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Malformed simulation settings on {Topic}: {Message}", message.Topic, ex.Message);
            return Task.CompletedTask;
        }

        // 設備端自己也檢查範圍，不盲目相信收到的值。
        if (envelope?.Payload is not { } settings || !(settings.TimeScale >= 1 && settings.TimeScale <= MaxTimeScale))
        {
            logger.LogWarning("Simulation settings on {Topic} are missing or out of range; ignored.", message.Topic);
            return Task.CompletedTask;
        }

        if (settings.TimeScale != fleet.TimeScale)
        {
            fleet.TimeScale = settings.TimeScale;
            logger.LogInformation("Simulation time scale is now x{TimeScale}.", settings.TimeScale);
        }

        return Task.CompletedTask;
    }
}
