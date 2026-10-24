using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Layout;

/// <summary>
/// 把使用中的佈局版本發布到佈局 Topic（retained，模擬器與 Unity 連上就能收到）。
/// 版本換了、或重新連上 Broker 時發布一次；不在佈局裡的車，先清掉它們在 Broker 上的舊狀態（retained 的空訊息）。
/// </summary>
public sealed class LayoutPublisher(
    ActiveLayout active,
    IMqttPublisher publisher,
    ILogger<LayoutPublisher> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(500);

    private long _publishedVersion = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!publisher.IsConnected)
                {
                    // 斷線期間的版本不一定送得出去：重新連上時再發一次。
                    _publishedVersion = -1;
                    continue;
                }

                var version = active.Version;
                if (version == _publishedVersion || active.Revision is not { } revision)
                {
                    continue;
                }

                try
                {
                    foreach (var vehicleId in active.RetiredVehicleIds)
                    {
                        await publisher.PublishRawAsync(
                            Topics.VehicleState(publisher.Settings.FactoryId, vehicleId),
                            ReadOnlyMemory<byte>.Empty,
                            MqttQualityOfServiceLevel.AtLeastOnce,
                            retain: true,
                            stoppingToken);
                    }

                    var envelope = MessageEnvelope.Create(publisher.Settings.ProducerId, revision, DateTimeOffset.UtcNow);
                    await publisher.PublishAsync(
                        Topics.Layout(publisher.Settings.FactoryId),
                        envelope,
                        MqttQualityOfServiceLevel.AtLeastOnce,
                        retain: true,
                        stoppingToken);
                    _publishedVersion = version;
                    logger.LogInformation("Published layout {Name} (version {Version}).", revision.Name, version);
                }
                catch (MqttNotConnectedException)
                {
                    _publishedVersion = -1;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Publishing the layout failed; retrying.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
