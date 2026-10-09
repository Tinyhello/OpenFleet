using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Api.Data;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Transport;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Traffic;

/// <summary>
/// 交通控制迴圈：每一輪把「車在哪裡、要去哪裡」交給 <see cref="TrafficController"/>，再把它決定的放行發給車輛。
/// 要去哪裡由派工狀態決定：已接受→取料點；已取料→卸料點；沒有任務→自己的充電車位。
/// 放行不經 Outbox：它是隨時會被下一輪取代的控制訊息，遺失了交通控制會自己重送。
/// </summary>
public sealed class TrafficWorker(
    IServiceScopeFactory scopes,
    FleetStateStore fleet,
    TrafficController traffic,
    RoadNetwork network,
    IMqttPublisher publisher,
    IOptions<FleetOptions> fleetOptions,
    IOptions<TrafficOptions> options,
    TimeProvider time,
    ILogger<TrafficWorker> logger) : BackgroundService
{
    private Dictionary<string, string>? _stationNodes;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ControlInterval, time);

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
                    logger.LogError(ex, "Traffic cycle failed; retrying next cycle.");
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
        if (vehicles.Count == 0)
        {
            return;
        }

        Dictionary<string, MovementGoal> assignments;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpenFleetDbContext>();

            // 站點不會在執行中改變：交接點對應的節點查一次就記住。
            _stationNodes ??= (await db.Stations.AsNoTracking().ToListAsync(cancellationToken))
                .ToDictionary(s => s.Code, s => network.Nearest(s.HandoffX, s.HandoffZ).Code, StringComparer.Ordinal);

            assignments = (await scope.ServiceProvider.GetRequiredService<TransportService>().GetMovementGoalsAsync(cancellationToken))
                .ToDictionary(g => g.VehicleId, StringComparer.Ordinal);
        }

        var inputs = vehicles.Select(vehicle =>
        {
            var (goal, canMove) = GoalFor(vehicle, assignments.GetValueOrDefault(vehicle.VehicleId));
            return new TrafficInput(
                vehicle.VehicleId,
                vehicle.Connectivity == VehicleConnectivity.Online,
                vehicle.TelemetrySessionId,
                vehicle.Position.X,
                vehicle.Position.Z,
                vehicle.RoutePlanId,
                vehicle.RouteRevision,
                vehicle.RouteReachedCount,
                goal,
                canMove);
        }).ToList();

        foreach (var (vehicleId, clearance) in traffic.Update(inputs))
        {
            var topic = Topics.Vehicle(publisher.Settings.FactoryId, vehicleId, VehicleChannel.Clearance);
            try
            {
                await publisher.PublishAsync(topic, MessageEnvelope.Create(publisher.Settings.ProducerId, clearance, time.GetUtcNow()),
                    MqttQualityOfServiceLevel.AtLeastOnce, retain: false, cancellationToken);
            }
            catch (MqttNotConnectedException)
            {
                // 車輛的遙測不會反映這次放行，交通控制之後會重送。
                return;
            }
        }
    }

    private (string? Goal, bool CanMove) GoalFor(VehicleState vehicle, MovementGoal? assignment)
    {
        if (assignment is null)
        {
            return fleetOptions.Value.Parking.TryGetValue(vehicle.VehicleId, out var slot)
                ? (network.Nearest(slot.X, slot.Z).Code, true)
                : (null, false);
        }

        var pickup = _stationNodes![assignment.FromStationCode];
        var destination = _stationNodes[assignment.ToStationCode];

        return assignment.Status switch
        {
            AssignmentStatus.Loaded => (destination, true),

            // 已在取料點上料中：先把往卸料點的路規劃出來（畫面可以預告），但等回報 Loaded 才放行。
            AssignmentStatus.Accepted when traffic.IsAt(vehicle.VehicleId, pickup) => (destination, false),
            AssignmentStatus.Accepted => (pickup, true),

            // 指令還沒被接受（或逾時待確認）：留在原地，不替它規劃路徑。
            _ => (null, false),
        };
    }
}
