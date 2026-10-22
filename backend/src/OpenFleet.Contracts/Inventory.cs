using System;
using System.Collections.Generic;

namespace OpenFleet.Contracts
{
    // P5 庫存與採購的 HTTP 契約。

    public enum StockMovementReason
    {
        /// <summary>啟動或示範重設時放進去的初始庫存。</summary>
        Seed,

        /// <summary>採購單收貨入庫。</summary>
        Receipt,

        /// <summary>盤點調整。</summary>
        Adjustment,

        /// <summary>工單領用已保留的量。</summary>
        Consumption,

        /// <summary>切料產出的備料入庫。</summary>
        Production,

        /// <summary>出貨後多出來的零件收進成品倉。</summary>
        Surplus,
    }

    public enum PurchaseOrderStatus
    {
        Ordered,
        Received,
        Cancelled,
    }

    /// <summary>一個品項在一個站點的庫存。</summary>
    public sealed class StockBalanceDto
    {
        public string ItemCode { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public ItemKind Kind { get; set; }
        public string StationCode { get; set; } = string.Empty;

        /// <summary>成品倉的現貨屬於哪一個客戶（依規格與客戶分開放）；圓棒與備料沒有。</summary>
        public string? Owner { get; set; }
        public int OnHand { get; set; }
        public int Reserved { get; set; }
        public int Available { get; set; }

        /// <summary>安全庫存；只有圓棒有設（使用者 2026-10-22：安全庫存只看圓棒）。</summary>
        public int? SafetyStock { get; set; }

        /// <summary>已下採購單、還沒收貨的量。</summary>
        public int OnOrder { get; set; }
        public bool BelowSafetyStock { get; set; }
    }

    /// <summary>
    /// 低於安全庫存的提醒。系統只提醒，要不要補貨由人決定；建議量是補到安全庫存還差多少（已扣掉訂了還沒到的）。
    /// </summary>
    public sealed class StockAlertDto
    {
        public string ItemCode { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public int Available { get; set; }
        public int SafetyStock { get; set; }
        public int OnOrder { get; set; }
        public int SuggestedQuantity { get; set; }
    }

    public sealed class StockMovementDto
    {
        public long Id { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string StationCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public StockMovementReason Reason { get; set; }
        public string? Reference { get; set; }
        public int OnHandAfter { get; set; }
        public DateTimeOffset At { get; set; }
    }

    /// <summary>盤點調整（POST /api/inventory/adjustments）：數量可正可負。</summary>
    public sealed class StockAdjustmentRequest
    {
        public string ItemCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>建立採購單（POST /api/purchase-orders）。可帶 Idempotency-Key 標頭，重送不會多一張。</summary>
    public sealed class CreatePurchaseOrderRequest
    {
        public string ItemCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public string? Supplier { get; set; }
    }

    public sealed class PurchaseOrderDto
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public string ItemCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public string? Supplier { get; set; }
        public PurchaseOrderStatus Status { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ReceivedAt { get; set; }
        public DateTimeOffset? CancelledAt { get; set; }
    }

    public sealed class InventorySummaryDto
    {
        public List<StockBalanceDto> Balances { get; set; } = new List<StockBalanceDto>();
        public List<StockAlertDto> Alerts { get; set; } = new List<StockAlertDto>();
    }
}
