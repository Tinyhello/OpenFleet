using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Transport;

/// <summary>
/// 車輛事件的「去重＋業務變更」放在同一筆資料庫交易（Scoped）。
/// Inbox 列和業務變更一起提交或一起回滾，所以不會出現「已標記處理但業務沒改」或重送造成第二次效果。
/// </summary>
public sealed class VehicleEventProcessor(OpenFleetDbContext db, TransportService transport, TimeProvider time)
{
    /// <summary>第一次處理回傳 true；重複的訊息回傳 false，而且不產生任何變更。</summary>
    public async Task<bool> ProcessAsync(string vehicleId, MessageEnvelope<VehicleEvent> envelope, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // ON CONFLICT DO NOTHING：重複時影響 0 列，不會丟出例外讓整筆交易作廢。
        var now = time.GetUtcNow();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "InboxMessages" ("ProducerId", "MessageId", "ProcessedAt")
             VALUES ({envelope.ProducerId}, {envelope.MessageId}, {now})
             ON CONFLICT DO NOTHING
             """,
            cancellationToken);
        if (inserted == 0)
        {
            return false;
        }

        // 沒有 AssignmentId 的事件（Ping、MoveTo）不影響業務資料，只需要去重。
        if (envelope.Payload.AssignmentId is not null)
        {
            await transport.ApplyVehicleEventAsync(vehicleId, envelope.Payload, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
