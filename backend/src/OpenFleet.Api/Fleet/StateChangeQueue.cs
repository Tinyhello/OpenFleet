using System.Threading.Channels;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Fleet;

/// <summary>
/// 狀態變更的生產者／消費者佇列。MQTT 收訊處理只負責更新狀態並放進佇列，
/// 對外發送由 <see cref="VehicleStatePublisher"/> 在另一個背景迴圈完成，收訊不會被發送網路 I/O 卡住。
/// 滿了就丟最舊的：車輛狀態只需要最新值。
/// </summary>
public sealed class StateChangeQueue
{
    private readonly Channel<VehicleState> _channel = Channel.CreateBounded<VehicleState>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    public ChannelReader<VehicleState> Reader => _channel.Reader;

    public void Enqueue(VehicleState state) => _channel.Writer.TryWrite(state);
}
