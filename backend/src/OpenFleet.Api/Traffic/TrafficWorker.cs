using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Api.Data;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Layout;
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
    ActiveLayout layout,
    LayoutGate gate,
    IMqttPublisher publisher,
    IOptions<TrafficOptions> options,
    TimeProvider time,
    ILogger<TrafficWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ControlInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // 套用佈局時暫停：等新佈局的資料都寫好，再從下一輪開始規劃。
                if (!gate.TryEnter())
                {
                    continue;
                }

                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Traffic cycle failed; retrying next cycle.");
                }
                finally
                {
                    gate.Exit();
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
        double emptySpeedFactor, loadedSpeedFactor;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpenFleetDbContext>();

            assignments = (await scope.ServiceProvider.GetRequiredService<TransportService>().GetMovementGoalsAsync(cancellationToken))
                .ToDictionary(g => g.VehicleId, StringComparer.Ordinal);

            // 測試模式下車輛可以開得比較快（空車與載貨各一個倍率）；倍率跟著放行送給車輛。
            var speeds = await db.ProductionSettings.AsNoTracking()
                .Select(s => new { Empty = s.TestMode ? s.TestEmptyVehicleSpeed : 1d, Loaded = s.TestMode ? s.TestLoadedVehicleSpeed : 1d })
                .SingleAsync(cancellationToken);
            emptySpeedFactor = speeds.Empty;
            loadedSpeedFactor = speeds.Loaded;
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
            clearance.EmptySpeedFactor = emptySpeedFactor;
            clearance.LoadedSpeedFactor = loadedSpeedFactor;
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
            // 沒有任務的車回自己的車位（車位是佈局的一部分，換佈局時車會被放回車位）。
            return layout.HomeNodeOf(vehicle.VehicleId) is { } home ? (home, true) : (null, false);
        }

        // 有效派工的目的站一定已經定好（派車時才定，P7 第 1a 步）；萬一沒有，這台車先不動，不替它規劃路徑。
        if (assignment.ToStationCode is null)
        {
            logger.LogWarning("Vehicle {VehicleId} has an active assignment without a destination station; holding it in place.", vehicle.VehicleId);
            return (null, false);
        }

        if (!layout.TryStationNode(assignment.FromStationCode, out var pickup)
            || !layout.TryStationNode(assignment.ToStationCode, out var destination))
        {
            logger.LogWarning("Vehicle {VehicleId} is assigned to a station outside the active layout; holding it in place.", vehicle.VehicleId);
            return (null, false);
        }

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
