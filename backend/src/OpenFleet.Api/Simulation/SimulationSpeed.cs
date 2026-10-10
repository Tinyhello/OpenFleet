using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Simulation;

/// <summary>
/// 測試用的模擬時間倍率（Singleton，只存在記憶體）。倍率不改變時鐘，而是縮短「需要經過多久」：
/// 後端的加工與換刀時間除以倍率，模擬器的行駛與上下料也依同一個倍率加快。
/// 指令有效期限、離線判定這類通訊上的時間不受影響。後端重啟後回到 1 倍。
/// </summary>
public sealed class SimulationSpeed(TimeProvider time)
{
    public const double MinTimeScale = 1;
    public const double MaxTimeScale = 30;

    private readonly Lock _gate = new();
    private double _timeScale = 1;
    private DateTimeOffset _changedAt = DateTimeOffset.MinValue;
    private long _version;

    /// <summary>
    /// 目前的倍率，以及最後一次變更的時間。變更時進行中的那一件從變更時間重新計時，
    /// 否則切到高倍率的瞬間會把「之前已經過的時間」也用新倍率計算，一口氣跳好幾件。
    /// </summary>
    public (double TimeScale, DateTimeOffset ChangedAt) Current
    {
        get
        {
            lock (_gate)
            {
                return (_timeScale, _changedAt);
            }
        }
    }

    /// <summary>每次變更加 1；發布端用來判斷要不要重新發布。</summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public void Set(double timeScale)
    {
        lock (_gate)
        {
            if (timeScale != _timeScale)
            {
                _timeScale = timeScale;
                _changedAt = time.GetUtcNow();
                _version++;
            }
        }
    }
}

/// <summary>
/// 把倍率以 retained 訊息發布給模擬器：變更時發一次，每次（重新）連上 Broker 也發一次，
/// 蓋掉 Broker 上可能留著的舊值（例如上次測試留下的 10 倍）。
/// </summary>
public sealed class SimulationSpeedPublisher(
    SimulationSpeed speed,
    IMqttPublisher publisher,
    TimeProvider time,
    ILogger<SimulationSpeedPublisher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        long? published = null;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!publisher.IsConnected)
                {
                    published = null;
                    continue;
                }

                var version = speed.Version;
                if (published == version)
                {
                    continue;
                }

                try
                {
                    var settings = new SimulationSettingsDto { TimeScale = speed.Current.TimeScale };
                    await publisher.PublishAsync(
                        Topics.SimulationState(publisher.Settings.FactoryId),
                        MessageEnvelope.Create(publisher.Settings.ProducerId, settings, time.GetUtcNow()),
                        MqttQualityOfServiceLevel.AtLeastOnce, retain: true, stoppingToken);
                    published = version;
                    logger.LogInformation("Simulation time scale published: x{TimeScale}.", settings.TimeScale);
                }
                catch (MqttNotConnectedException)
                {
                    published = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public static class SimulationEndpoints
{
    public static IEndpointRouteBuilder MapSimulationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/simulation", (SimulationSpeed speed) => new SimulationSettingsDto { TimeScale = speed.Current.TimeScale });

        app.MapPut("/api/simulation", (SimulationSettingsDto request, SimulationSpeed speed) =>
        {
            // 寫成「不在範圍內」的否定，NaN 也會被擋下。
            if (!(request.TimeScale >= SimulationSpeed.MinTimeScale && request.TimeScale <= SimulationSpeed.MaxTimeScale))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["timeScale"] = [$"Time scale must be between {SimulationSpeed.MinTimeScale} and {SimulationSpeed.MaxTimeScale}."],
                });
            }

            speed.Set(request.TimeScale);
            return Results.Ok(new SimulationSettingsDto { TimeScale = speed.Current.TimeScale });
        });

        return app;
    }
}
