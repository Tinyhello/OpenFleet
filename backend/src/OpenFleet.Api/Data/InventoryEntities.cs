using OpenFleet.Contracts;

namespace OpenFleet.Api.Data;

/// <summary>
/// 一個品項在一個站點的庫存：在庫、已保留、可用（在庫 − 已保留）。
/// 保留是先占住、還沒拿走；真正領用時在庫與保留一起扣。
/// 用 xmin 做樂觀並行控制：兩個工作單元同時改同一列時，後存的那個會失敗並重試，所以保留的總數不會超過在庫。
/// </summary>
public sealed class StockBalance
{
    public Guid Id { get; private set; }
    public string ItemCode { get; private set; } = string.Empty;
    public string StationCode { get; private set; } = string.Empty;

    /// <summary>成品倉的現貨屬於哪一個客戶（使用者 2026-10-22：依規格與客戶分開放）；圓棒與備料是 null。</summary>
    public string? Owner { get; private set; }
    public int OnHand { get; private set; }
    public int Reserved { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public int Available => OnHand - Reserved;

    public static StockBalance Create(string itemCode, string stationCode, DateTimeOffset now, string? owner = null) =>
        new() { Id = Guid.NewGuid(), ItemCode = itemCode, StationCode = stationCode, Owner = owner, UpdatedAt = now };

    public void Add(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity must be positive.");
        }

        OnHand += quantity;
        UpdatedAt = now;
    }

    /// <summary>盤點調整：可以加也可以減，但不能減到比已保留的還少。</summary>
    public void Adjust(int delta, DateTimeOffset now)
    {
        if (OnHand + delta < Reserved)
        {
            throw new InvalidOperationException($"Stock of {ItemCode} cannot go below the reserved {Reserved}.");
        }

        OnHand += delta;
        UpdatedAt = now;
    }

    /// <summary>保留最多 <paramref name="wanted"/> 個，可用的不夠就保留可用的全部；回傳實際保留的數量。</summary>
    public int Reserve(int wanted, DateTimeOffset now)
    {
        var taken = Math.Max(0, Math.Min(wanted, Available));
        Reserved += taken;
        UpdatedAt = now;
        return taken;
    }

    public void Release(int quantity, DateTimeOffset now)
    {
        if (quantity < 0 || quantity > Reserved)
        {
            throw new InvalidOperationException($"Cannot release {quantity} of {ItemCode}; only {Reserved} reserved.");
        }

        Reserved -= quantity;
        UpdatedAt = now;
    }

    /// <summary>領用已保留的量：在庫與保留一起扣。</summary>
    public void Consume(int quantity, DateTimeOffset now)
    {
        if (quantity < 0 || quantity > Reserved)
        {
            throw new InvalidOperationException($"Cannot consume {quantity} of {ItemCode}; only {Reserved} reserved.");
        }

        Reserved -= quantity;
        OnHand -= quantity;
        UpdatedAt = now;
    }
}

/// <summary>
/// 一筆保留：誰（例如哪一張工單）占住了多少。領用或取消時扣掉，剩 0 就算結束。
/// </summary>
public sealed class StockReservation
{
    public Guid Id { get; private set; }
    public Guid StockBalanceId { get; private set; }
    public string ItemCode { get; private set; } = string.Empty;

    /// <summary>保留的來源，例如工單號碼。</summary>
    public string Reference { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public int Remaining { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    public static StockReservation Create(StockBalance balance, int quantity, string reference, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        StockBalanceId = balance.Id,
        ItemCode = balance.ItemCode,
        Reference = reference,
        Quantity = quantity,
        Remaining = quantity,
        CreatedAt = now,
    };

    public void Take(int quantity, DateTimeOffset now)
    {
        if (quantity < 0 || quantity > Remaining)
        {
            throw new InvalidOperationException($"Reservation {Id} has only {Remaining} left.");
        }

        Remaining -= quantity;
        if (Remaining == 0)
        {
            ClosedAt = now;
        }
    }
}

/// <summary>庫存異動帳（只新增）。數量有正負：入庫為正、領用為負。</summary>
public sealed class StockMovement
{
    public long Id { get; set; }
    public required string ItemCode { get; set; }
    public required string StationCode { get; set; }
    public int Quantity { get; set; }
    public StockMovementReason Reason { get; set; }
    public string? Reference { get; set; }
    public int OnHandAfter { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>
/// 採購單：由人建立（使用者 2026-10-22：低於安全庫存只提醒，要不要補貨由人決定），只買圓棒。
/// 到貨時由人收貨，收貨才入庫；還沒到的不算可用。一張單一個品項。
/// </summary>
public sealed class PurchaseOrder
{
    public Guid Id { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public string ItemCode { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public string? Supplier { get; private set; }
    public PurchaseOrderStatus Status { get; private set; }

    /// <summary>建立時帶的 Idempotency-Key；同一個鍵重送只會有一張單。</summary>
    public string? RequestKey { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReceivedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public uint Version { get; private set; }

    public static PurchaseOrder Create(string itemCode, int quantity, string? supplier, string? requestKey, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Number = $"PO-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
        ItemCode = itemCode,
        Quantity = quantity,
        Supplier = supplier,
        Status = PurchaseOrderStatus.Ordered,
        RequestKey = requestKey,
        CreatedAt = now,
    };

    public void Receive(DateTimeOffset now)
    {
        RequireOrdered(nameof(Receive));
        Status = PurchaseOrderStatus.Received;
        ReceivedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        RequireOrdered(nameof(Cancel));
        Status = PurchaseOrderStatus.Cancelled;
        CancelledAt = now;
    }

    private void RequireOrdered(string action)
    {
        if (Status != PurchaseOrderStatus.Ordered)
        {
            throw new InvalidOperationException($"Cannot {action} purchase order {Number} in status {Status}.");
        }
    }
}
