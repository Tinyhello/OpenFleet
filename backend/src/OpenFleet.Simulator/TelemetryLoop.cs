using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Simulator;

/// <summary>
/// 固定間隔推進模擬，發送遙測（QoS 0，斷線時直接略過，不補送舊位置）與這一步產生的回報（QoS 1，斷線時保留到重連後補送）。
/// </summary>
public sealed class TelemetryLoop(
    SimulatedFleet fleet,
    EventSender sender,
    IMqttPublisher publisher,
    IOptions<SimulatorSettings> settings,
    TimeProvider time,
    ILogger<TelemetryLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Simulating {Count} vehicle(s), session {SessionId}.", settings.Value.VehicleIds.Count, fleet.SessionId);

        using var timer = new PeriodicTimer(settings.Value.TelemetryInterval, time);
        var last = time.GetTimestamp();

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = time.GetTimestamp();
                var samples = fleet.Advance(time.GetElapsedTime(last, now));
                last = now;

                if (!publisher.IsConnected)
                {
                    continue;
                }

                await sender.FlushAsync(publisher, stoppingToken);

                foreach (var (vehicleId, telemetry) in samples)
                {
                    var envelope = MessageEnvelope.Create(publisher.Settings.ProducerId, telemetry, time.GetUtcNow());
                    var topic = Topics.Vehicle(publisher.Settings.FactoryId, vehicleId, VehicleChannel.Telemetry);
                    try
                    {
                        await publisher.PublishAsync(topic, envelope, MqttQualityOfServiceLevel.AtMostOnce, retain: false, stoppingToken);
                    }
                    catch (MqttNotConnectedException)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
