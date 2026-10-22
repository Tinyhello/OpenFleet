using System;
using System.Collections.Generic;

namespace OpenFleet.Contracts
{
    // P5 客戶訂單與生產工單的 HTTP 契約。

    public enum ProductionOrderKind
    {
        /// <summary>一行訂單明細開一張主工單。</summary>
        Main,

        /// <summary>品管報廢後另開的補做工單，連回主工單。</summary>
        Makeup,
    }

    public enum SalesOrderStatus
    {
        /// <summary>還有零件在做（含補做）。</summary>
        InProduction,

        /// <summary>每一行明細的每一種零件都做完了（包裝完成）。</summary>
        Completed,
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

    /// <summary>一種零件的進度。完成＝包裝完成；製作中＝還在原料倉、路上或各站（含等料）。</summary>
    public sealed class PartProgressDto
    {
        public string PartCode { get; set; } = string.Empty;
        public int Ordered { get; set; }
        public int Finished { get; set; }
        public int InProgress { get; set; }
        public int Scrapped { get; set; }

        /// <summary>為了報廢另開的補做數量。</summary>
        public int Makeup { get; set; }
    }

    /// <summary>報廢註記（記在主工單上）。</summary>
    public sealed class ScrapNoteDto
    {
        public string SourceOrderNumber { get; set; } = string.Empty;
        public string LotNumber { get; set; } = string.Empty;
        public string PartCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public string MakeupOrderNumber { get; set; } = string.Empty;
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

    public sealed class SalesOrderDto
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public string Customer { get; set; } = string.Empty;
        public DateTime? DueDate { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public SalesOrderStatus Status { get; set; }
        public List<SalesOrderLineDto> Lines { get; set; } = new List<SalesOrderLineDto>();
    }
}
