using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Api.Fleet;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Transport;

/// <summary>
/// 派工迴圈（Singleton 的背景服務）。每一輪建立新的 DI scope 取得 Scoped 的 DbContext 與服務，
/// 一輪結束就釋放；單一輪失敗只記錄，不會讓整個服務停下來。
/// </summary>
public sealed class DispatchWorker(
    IServiceScopeFactory scopes,
    FleetStateStore fleet,
    IMqttPublisher publisher,
    IOptions<FleetOptions> options,
    TimeProvider time,
    ILogger<DispatchWorker> logger) : BackgroundService
{
    private const double ParkedTolerance = 0.3;

    private readonly Dictionary<string, DateTimeOffset> _parkRequestedAt = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.DispatchInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Dispatch cycle failed; retrying next cycle.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var vehicles = fleet.Snapshot();
        IReadOnlyCollection<string> busy;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var transport = scope.ServiceProvider.GetRequiredService<TransportService>();
            await transport.MarkExpiredAssignmentsAsync(cancellationToken);
            await transport.DispatchPendingAsync(vehicles, cancellationToken);
            busy = await transport.GetBusyVehicleIdsAsync(cancellationToken);
        }

        await ParkIdleVehiclesAsync(vehicles, busy, cancellationToken);
    }

    /// <summary>
    /// 沒有任務、已停止、又不在自己車位上的車輛，送回充電維護區待命。
    /// 這不是派工：直接發 MoveTo（不經 Outbox），遺失了下一輪會再發；車輛收到新的搬運指令時可以中途取代它。
    /// </summary>
    private async Task ParkIdleVehiclesAsync(IReadOnlyList<VehicleState> vehicles, IReadOnlyCollection<string> busy, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();

        foreach (var vehicle in vehicles)
        {
            if (vehicle.Connectivity != VehicleConnectivity.Online
                || vehicle.Speed > 0
                || busy.Contains(vehicle.VehicleId)
                || !settings.Parking.TryGetValue(vehicle.VehicleId, out var slot)
                || Math.Sqrt(Math.Pow(vehicle.Position.X - slot.X, 2) + Math.Pow(vehicle.Position.Z - slot.Z, 2)) <= ParkedTolerance)
            {
                continue;
            }

            // 上一道 MoveTo 還在有效期內就不重發（車輛起步前會有幾次回報仍是靜止）。
            if (_parkRequestedAt.TryGetValue(vehicle.VehicleId, out var requestedAt) && now - requestedAt < settings.CommandTimeToLive)
            {
                continue;
            }

            var command = new VehicleCommand
            {
                CommandId = Guid.NewGuid(),
                CommandType = VehicleCommandType.MoveTo,
                ExpiresAt = now + settings.CommandTimeToLive,
                Destination = new Position3 { X = slot.X, Z = slot.Z },
            };
            var topic = Topics.Vehicle(publisher.Settings.FactoryId, vehicle.VehicleId, VehicleChannel.Commands);

            try
            {
                await publisher.PublishAsync(topic, MessageEnvelope.Create(publisher.Settings.ProducerId, command, now),
                    MqttQualityOfServiceLevel.AtLeastOnce, retain: false, cancellationToken);
                _parkRequestedAt[vehicle.VehicleId] = now;
                logger.LogInformation("Vehicle {VehicleId} is idle; sent to parking slot ({X}, {Z}).", vehicle.VehicleId, slot.X, slot.Z);
            }
            catch (MqttNotConnectedException)
            {
                return;
            }
        }
    }
}
