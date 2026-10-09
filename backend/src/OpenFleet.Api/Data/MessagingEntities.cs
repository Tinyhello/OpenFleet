namespace OpenFleet.Api.Data;

/// <summary>
/// Transactional Outbox：要送出的 MQTT 訊息和業務變更寫在同一筆交易裡，
/// 之後由 OutboxPublisher 發送。這樣不會出現「派工已存檔但指令沒送出」或相反的情況。
/// </summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }

    /// <summary>外層 MessageId。重送時 Payload 不變，接收端可以去重。</summary>
    public Guid MessageId { get; set; }
    public required string Topic { get; set; }

    /// <summary>已序列化的完整 JSON（含外層）。</summary>
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

/// <summary>
/// 已處理過的事件。主鍵 (ProducerId, MessageId) 就是去重規則：
/// QoS 1 重送時第二次 INSERT 會衝突，整個處理直接略過；服務重啟後仍然有效。
/// </summary>
public sealed class InboxMessage
{
    public required string ProducerId { get; set; }
    public Guid MessageId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
