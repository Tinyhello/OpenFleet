using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Fleet;

/// <summary>
/// 把 <see cref="StateChangeQueue"/> 的變更發布到 state Topic（retained，新訂閱者會立刻收到最新值）。
/// 斷線期間的變更直接丟棄，重新連線後改發一次完整快照補齊，而不是累積大量過時訊息。
/// </summary>
public sealed class VehicleStatePublisher(
    StateChangeQueue changes,
    FleetStateStore store,
    IMqttPublisher publisher,
    ILogger<VehicleStatePublisher> logger) : BackgroundService
{
    private static readonly TimeSpan IdleWake = TimeSpan.FromSeconds(1);

    private bool _needsFullResync = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = changes.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            // 沒有變更時也定期醒來，才能在「重連後但沒有新變更」時補發快照。
            using (var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                wake.CancelAfter(IdleWake);
                try
                {
                    await reader.WaitToReadAsync(wake.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }
            }

            if (!publisher.IsConnected)
            {
                _needsFullResync = true;
                Drain(reader);
                continue;
            }

            try
            {
                if (_needsFullResync)
                {
                    // 先清掉佇列再取快照：快照已包含這些變更，之後進來的新變更照常發送。
                    Drain(reader);
                    foreach (var state in store.Snapshot())
                    {
                        await PublishAsync(state, stoppingToken);
                    }

                    _needsFullResync = false;
                }

                while (reader.TryRead(out var state))
                {
                    await PublishAsync(state, stoppingToken);
                }
            }
            catch (MqttNotConnectedException)
            {
                _needsFullResync = true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Publishing vehicle state failed.");
                _needsFullResync = true;
            }
        }
    }

    private Task PublishAsync(VehicleState state, CancellationToken cancellationToken)
    {
        var envelope = MessageEnvelope.Create(publisher.Settings.ProducerId, state, DateTimeOffset.UtcNow);
        var topic = Topics.VehicleState(publisher.Settings.FactoryId, state.VehicleId);
        return publisher.PublishAsync(topic, envelope, MqttQualityOfServiceLevel.AtLeastOnce, retain: true, cancellationToken);
    }

    private static void Drain(System.Threading.Channels.ChannelReader<VehicleState> reader)
    {
        while (reader.TryRead(out _))
        {
        }
    }
}
