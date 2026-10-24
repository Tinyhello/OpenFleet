using Microsoft.Extensions.Options;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Layout;

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
    LayoutGate gate,
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
                // 套用佈局時暫停（見 LayoutGate）。
                if (!gate.TryEnter())
                {
                    continue;
                }

                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var transport = scope.ServiceProvider.GetRequiredService<TransportService>();
                    var vehicles = fleet.Snapshot();
                    await transport.MarkExpiredAssignmentsAsync(stoppingToken);
                    // 先對帳再派工：設備弄丟的搬運在這一輪就能重新排進去。
                    await transport.ReconcileAsync(vehicles, stoppingToken);
                    await transport.DispatchPendingAsync(vehicles, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Dispatch cycle failed; retrying next cycle.");
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
}
