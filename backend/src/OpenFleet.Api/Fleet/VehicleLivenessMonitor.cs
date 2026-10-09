using Microsoft.Extensions.Options;

namespace OpenFleet.Api.Fleet;

/// <summary>定期檢查車輛最後回報時間，逾時標記為 Offline。</summary>
public sealed class VehicleLivenessMonitor(
    FleetStateStore store,
    IOptions<FleetOptions> options,
    TimeProvider time,
    ILogger<VehicleLivenessMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(settings.LivenessCheckInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var changed = store.MarkStaleVehiclesOffline(settings.OfflineAfter);
                if (changed > 0)
                {
                    logger.LogWarning("{Count} vehicle(s) marked offline (no report for {OfflineAfter}).", changed, settings.OfflineAfter);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
