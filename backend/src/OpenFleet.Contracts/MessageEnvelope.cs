using System;

namespace OpenFleet.Contracts
{
    /// <summary>
    /// 所有 MQTT 訊息的共用外層。業務內容放在 <see cref="Payload"/>，
    /// 去重、追蹤與版本判斷只看外層欄位。
    /// </summary>
    public sealed class MessageEnvelope<TPayload>
    {
        public int SchemaVersion { get; set; } = ContractVersion.Current;

        /// <summary>每則訊息唯一；重送同一則訊息時必須沿用同一個值，接收端以此去重。</summary>
        public Guid MessageId { get; set; }

        /// <summary>發送者身分，例如 backend、simulator。與 MessageId 組成去重鍵。</summary>
        public string ProducerId { get; set; } = string.Empty;

        /// <summary>回應時填入觸發它的訊息 MessageId，用來串起一次完整流程。</summary>
        public Guid? CorrelationId { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public TPayload Payload { get; set; } = default!;
    }

    public static class ContractVersion
    {
        public const int Current = 1;
    }

    public static class MessageEnvelope
    {
        public static MessageEnvelope<T> Create<T>(string producerId, T payload, DateTimeOffset occurredAt, Guid? correlationId = null)
        {
            return new MessageEnvelope<T>
            {
                MessageId = Guid.NewGuid(),
                ProducerId = producerId,
                CorrelationId = correlationId,
                OccurredAt = occurredAt,
                Payload = payload,
            };
        }
    }
}
