namespace OpenFleet.Api.Fleet;

/// <summary>
/// 以 (ProducerId, MessageId) 去重：QoS 1 保證「至少一次」，同一則訊息可能收到多次，
/// 但業務效果只能發生一次。P1 放在記憶體且有容量上限；P2 改為資料庫 InboxMessage 唯一鍵，
/// 並與業務變更放在同一筆交易，服務重啟後仍有效。
/// </summary>
public sealed class InboxDeduplicator
{
    private const int Capacity = 10_000;

    private readonly Lock _gate = new();
    private readonly HashSet<(string ProducerId, Guid MessageId)> _seen = [];
    private readonly Queue<(string ProducerId, Guid MessageId)> _order = new();

    /// <summary>第一次看到回傳 true；重複則回傳 false。</summary>
    public bool TryRegister(string producerId, Guid messageId)
    {
        var key = (producerId, messageId);
        lock (_gate)
        {
            if (!_seen.Add(key))
            {
                return false;
            }

            _order.Enqueue(key);
            if (_order.Count > Capacity)
            {
                _seen.Remove(_order.Dequeue());
            }

            return true;
        }
    }
}
