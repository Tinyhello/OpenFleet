using Microsoft.Extensions.Options;
using OpenFleet.Api.Layout;

namespace OpenFleet.Api.Production;

/// <summary>對應 "Production" 設定區段。</summary>
public sealed class ProductionOptions
{
    public const string SectionName = "Production";

    /// <summary>多久檢查一次加工進度；加工完成時間的誤差不會超過這個間隔。</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// 定期推進加工進度的背景服務。加工的到期時間存在資料庫（DueAt），不是記憶體裡的計時器，
/// 所以後端重啟後會從資料庫接續，不會遺失進行中的加工。
/// </summary>
public sealed class ProductionWorker(
    IServiceScopeFactory scopes,
    IOptions<ProductionOptions> options,
    LayoutGate gate,
    TimeProvider time,
    ILogger<ProductionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.TickInterval, time);

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
                    await scope.ServiceProvider.GetRequiredService<ProductionService>().AdvanceOperationsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Production cycle failed; retrying next cycle.");
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
