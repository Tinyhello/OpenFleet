using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Simulator;

/// <summary>
/// 把 <see cref="SimulatedFleet"/> 佇列中的回報依序發送出去。指令處理與模擬迴圈都會呼叫，
/// 所以用 SemaphoreSlim 確保同一時間只有一個發送者，順序才不會亂（lock 不能跨 await，SemaphoreSlim 可以）。
/// </summary>
public sealed class EventSender(SimulatedFleet fleet)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task FlushAsync(IMqttPublisher publisher, CancellationToken cancellationToken)
    {
        if (!publisher.IsConnected)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            while (fleet.TryPeekEvent(out var vehicleId, out var envelope))
            {
                var topic = Topics.Vehicle(publisher.Settings.FactoryId, vehicleId, VehicleChannel.Events);
                await publisher.PublishAsync(topic, envelope, MqttQualityOfServiceLevel.AtLeastOnce, retain: false, cancellationToken);

                // 發送成功才移除；失敗時留在佇列，下次從同一則繼續。
                fleet.CompleteEvent();
            }
        }
        catch (MqttNotConnectedException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }
}
