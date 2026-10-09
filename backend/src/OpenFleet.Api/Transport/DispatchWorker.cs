using Microsoft.Extensions.Options;
using OpenFleet.Api.Fleet;

namespace OpenFleet.Api.Transport;

/// <summary>
/// 派工迴圈（Singleton 的背景服務）。每一輪建立新的 DI scope 取得 Scoped 的 DbContext 與服務，
/// 一輪結束就釋放；單一輪失敗只記錄，不會讓整個服務停下來。
/// 這裡只決定「哪個任務給哪台車」；車怎麼開過去由 TrafficWorker 負責。
/// </summary>
public sealed class DispatchWorker(
    IServiceScopeFactory scopes,
    FleetStateStore fleet,
    IOptions<FleetOptions> options,
    TimeProvider time,
    ILogger<DispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.DispatchInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var transport = scope.ServiceProvider.GetRequiredService<TransportService>();
                    await transport.MarkExpiredAssignmentsAsync(stoppingToken);
                    await transport.DispatchPendingAsync(fleet.Snapshot(), stoppingToken);
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
}
