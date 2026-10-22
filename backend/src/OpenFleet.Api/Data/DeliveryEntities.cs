namespace OpenFleet.Api.Data;

/// <summary>
/// 一次出貨（出貨單）：整張客戶訂單一起出貨（使用者 2026-10-22），或負責人同意的那一次分批（只出完整的組，最多分一次）。
/// 每一行明細的每一種零件各裝一箱（<see cref="DeliveryLine"/>），箱子都到了出貨站，下一班貨車才一起載走。
/// </summary>
public sealed class Delivery
{
    public Guid Id { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public Guid SalesOrderId { get; private set; }
    public bool IsPartial { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ShippedAt { get; private set; }
    public List<DeliveryLine> Lines { get; private set; } = [];

    /// <summary>出貨單號：訂單號碼的後半段加上這是這張訂單的第幾次出貨。</summary>
    public static Delivery Create(SalesOrder order, int sequence, bool isPartial, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Number = $"DL-{order.NumberSuffix}-{sequence}",
        SalesOrderId = order.Id,
        IsPartial = isPartial,
        CreatedAt = now,
    };

    public DeliveryLine AddLine(SalesOrderLine line, string partCode, int quantity, MaterialLot box)
    {
        var deliveryLine = new DeliveryLine
        {
            Id = Guid.NewGuid(),
            DeliveryId = Id,
            SalesOrderLineId = line.Id,
            PartCode = partCode,
            Quantity = quantity,
            BoxLotId = box.Id,
        };
        Lines.Add(deliveryLine);
        return deliveryLine;
    }

    public void MarkShipped(DateTimeOffset now)
    {
        if (ShippedAt is not null)
        {
            throw new InvalidOperationException($"Delivery {Number} was already shipped.");
        }

        ShippedAt = now;
    }
}

/// <summary>出貨單的一行：哪一行明細的哪一種零件、幾支，裝在哪一箱。</summary>
public sealed class DeliveryLine
{
    public Guid Id { get; set; }
    public Guid DeliveryId { get; set; }
    public Guid SalesOrderLineId { get; set; }
    public required string PartCode { get; set; }
    public int Quantity { get; set; }
    public Guid BoxLotId { get; set; }
}

/// <summary>
/// 多出來的零件送進成品倉（使用者 2026-10-22：開工時多做的，出貨後用剩的依規格與客戶收進成品倉，只是存放）。
/// 整張訂單出貨時，每一行明細的每一種零件多出來的裝一箱送去成品倉；箱子到了，數量記進成品倉的庫存（依客戶分開）。
/// </summary>
public sealed class SurplusTransfer
{
    public Guid Id { get; private set; }
    public Guid BoxLotId { get; private set; }
    public Guid SalesOrderId { get; private set; }
    public Guid SalesOrderLineId { get; private set; }
    public string Customer { get; private set; } = string.Empty;

    /// <summary>零件品項（規格加零件），成品倉的庫存用它記。</summary>
    public string ItemCode { get; private set; } = string.Empty;
    public string PartCode { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>箱子送到成品倉、記進庫存的時間；還在路上時是 null。</summary>
    public DateTimeOffset? ReceivedAt { get; private set; }

    public static SurplusTransfer Create(
        SalesOrder order, SalesOrderLine line, string itemCode, string partCode, MaterialLot box, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        BoxLotId = box.Id,
        SalesOrderId = order.Id,
        SalesOrderLineId = line.Id,
        Customer = order.Customer,
        ItemCode = itemCode,
        PartCode = partCode,
        Quantity = box.Quantity,
        CreatedAt = now,
    };

    public void Receive(DateTimeOffset now)
    {
        if (ReceivedAt is not null)
        {
            throw new InvalidOperationException($"Surplus transfer {Id} was already received.");
        }

        ReceivedAt = now;
    }
}
