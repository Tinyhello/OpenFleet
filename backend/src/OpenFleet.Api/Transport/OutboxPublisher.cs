using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Api.Data;
using OpenFleet.Api.Fleet;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Transport;

/// <summary>
/// 把 Outbox 裡尚未發送的訊息依序送到 MQTT，送出後才標記 PublishedAt。
/// 「送出成功但標記前當機」時會再送一次（至少一次）；Payload 與 MessageId 不變，設備端以 CommandId 去重。
/// Broker 斷線時訊息留在資料表，恢復後補送；過期的指令由設備回報 Rejected。
/// </summary>
public sealed class OutboxPublisher(
    IServiceScopeFactory scopes,
    IMqttPublisher publisher,
    IOptions<FleetOptions> options,
    TimeProvider time,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.OutboxPollInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!publisher.IsConnected)
                {
                    continue;
                }

                try
                {
                    await PublishPendingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Outbox publishing failed; retrying next cycle.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenFleetDbContext>();

        var messages = await db.OutboxMessages
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            try
            {
                await publisher.PublishRawAsync(message.Topic, Encoding.UTF8.GetBytes(message.Payload),
                    MqttQualityOfServiceLevel.AtLeastOnce, retain: false, cancellationToken);
            }
            catch (MqttNotConnectedException)
            {
                break;
            }

            message.PublishedAt = time.GetUtcNow();
            // 每送一則就存檔：中途失敗時，已送出的不會在下一輪全部重送。
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
