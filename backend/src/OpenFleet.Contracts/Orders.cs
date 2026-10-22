using System;
using System.Collections.Generic;

namespace OpenFleet.Contracts
{
    // P5 客戶訂單、生產工單與出貨的 HTTP 契約。

    public enum ProductionOrderKind
    {
        /// <summary>一行訂單明細開一張主工單。</summary>
        Main,

        /// <summary>品管報廢、多做的又不夠抵時另開的補做工單，連回主工單。</summary>
        Makeup,
    }

    public enum SalesOrderStatus
    {
        /// <summary>還有零件在做（含補做），也還沒出過貨。</summary>
        InProduction,

        /// <summary>負責人同意分批，已經先出了一批；剩下的還在做。</summary>
        PartiallyShipped,

        /// <summary>每一行明細的每一種零件都做完了（包裝完成），等貨車。</summary>
        Completed,

        /// <summary>整張訂單都出貨了。</summary>
        Shipped,
    }

    /// <summary>建立客戶訂單（POST /api/sales-orders）。可帶 Idempotency-Key 標頭，重送不會多一張。</summary>
    public sealed class CreateSalesOrderRequest
    {
        public string Customer { get; set; } = string.Empty;

        /// <summary>交期（只取日期）；可以不填。</summary>
        public DateTime? DueDate { get; set; }
        public List<CreateSalesOrderLineRequest> Lines { get; set; } = new List<CreateSalesOrderLineRequest>();
    }

    public sealed class CreateSalesOrderLineRequest
    {
        /// <summary>整組沖模（規格代碼，例如 P30-HX10）或單一零件（例如 P30-HX10-U）。</summary>
        public string ItemCode { get; set; } = string.Empty;

        /// <summary>組數或支數，最多 300。</summary>
        public int Quantity { get; set; }
    }

    /// <summary>負責人同意這張訂單分批出貨（POST /api/sales-orders/{id}/partial-shipment）。一張訂單只能同意一次。</summary>
    public sealed class PartialShipmentRequest
    {
        public string ApprovedBy { get; set; } = string.Empty;
    }

    /// <summary>一種零件的進度。完成＝包裝完成（含之後出貨或收進成品倉的）；製作中＝還在原料倉、路上或各站（含等料）。</summary>
    public sealed class PartProgressDto
    {
        public string PartCode { get; set; } = string.Empty;
        public int Ordered { get; set; }

        /// <summary>開工時多做的數量（超過 30 組多 1 組、超過 60 組多 2 組）。</summary>
        public int Extra { get; set; }
        public int Finished { get; set; }
        public int InProgress { get; set; }
        public int Scrapped { get; set; }

        /// <summary>報廢後另開補做工單的數量（多做的不夠抵的部分）。</summary>
        public int Makeup { get; set; }
        public int Shipped { get; set; }

        /// <summary>出貨後多出來、收進成品倉的數量。</summary>
        public int Surplus { get; set; }
    }

    /// <summary>報廢註記（記在主工單上）。先用多做的抵，不夠的才開補做工單。</summary>
    public sealed class ScrapNoteDto
    {
        public string SourceOrderNumber { get; set; } = string.Empty;
        public string LotNumber { get; set; } = string.Empty;
        public string PartCode { get; set; } = string.Empty;
        public int Quantity { get; set; }

        /// <summary>其中由多做的抵掉的數量。</summary>
        public int CoveredByExtra { get; set; }

        /// <summary>多做的不夠抵時開的補做工單；全部抵掉時是 null。</summary>
        public string? MakeupOrderNumber { get; set; }
        public DateTimeOffset At { get; set; }
    }

    /// <summary>工單開立時存下來的規格。加工秒數只有覆寫的才有值，沒有覆寫的沿用製程設定。</summary>
    public sealed class ProductionSpecDto
    {
        public string SpecCode { get; set; } = string.Empty;
        public string SpecName { get; set; } = string.Empty;
        public int RodDiameterMm { get; set; }
        public int PiecesPerBin { get; set; }
        public int DieOuterDiameterMm { get; set; }
        public Dictionary<string, int> BlankLengthMm { get; set; } = new Dictionary<string, int>();
        public double? RoughSecondsPerPiece { get; set; }
        public double? FinishSecondsPerPiece { get; set; }
    }

    public sealed class ProductionOrderDto
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public ProductionOrderKind Kind { get; set; }
        public string? ParentNumber { get; set; }
        public string SalesOrderNumber { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string? PartCode { get; set; }
        public int Quantity { get; set; }

        /// <summary>主工單：每種零件多做幾支（或幾組）。</summary>
        public int ExtraQuantity { get; set; }
        public int BomVersion { get; set; }
        public ProductionSpecDto Spec { get; set; } = new ProductionSpecDto();
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>這張工單自己的框都已經包裝完或報廢。</summary>
        public bool IsComplete { get; set; }
        public List<PartProgressDto> Parts { get; set; } = new List<PartProgressDto>();

        /// <summary>主工單：報廢註記（含補做工單裡又報廢的）。</summary>
        public List<ScrapNoteDto> ScrapNotes { get; set; } = new List<ScrapNoteDto>();

        /// <summary>只有查單一工單時才帶。</summary>
        public List<LotDto>? Lots { get; set; }
    }

    public sealed class SalesOrderLineDto
    {
        public int LineNumber { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public ItemKind Kind { get; set; }
        public int Quantity { get; set; }
        public bool IsComplete { get; set; }

        /// <summary>每種零件的進度，主工單與補做工單合計。</summary>
        public List<PartProgressDto> Parts { get; set; } = new List<PartProgressDto>();
        public List<ProductionOrderDto> ProductionOrders { get; set; } = new List<ProductionOrderDto>();
    }

    public sealed class DeliveryLineDto
    {
        public int LineNumber { get; set; }
        public string PartCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public string BoxLotNumber { get; set; } = string.Empty;
    }

    /// <summary>一次出貨：整張訂單一起出，或負責人同意的那一次分批（只出完整的組）。</summary>
    public sealed class DeliveryDto
    {
        public string Number { get; set; } = string.Empty;
        public bool IsPartial { get; set; }
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>貨車載走的時間；還在路上或在出貨站等貨車時是 null。</summary>
        public DateTimeOffset? ShippedAt { get; set; }
        public List<DeliveryLineDto> Lines { get; set; } = new List<DeliveryLineDto>();
    }

    public sealed class SalesOrderDto
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public string Customer { get; set; } = string.Empty;
        public DateTime? DueDate { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public SalesOrderStatus Status { get; set; }

        /// <summary>同意分批的負責人與時間；沒有同意過是 null。</summary>
        public string? PartialApprovedBy { get; set; }
        public DateTimeOffset? PartialApprovedAt { get; set; }
        public List<SalesOrderLineDto> Lines { get; set; } = new List<SalesOrderLineDto>();
        public List<DeliveryDto> Deliveries { get; set; } = new List<DeliveryDto>();
    }
}
