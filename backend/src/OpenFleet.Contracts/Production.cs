using System;
using System.Collections.Generic;

namespace OpenFleet.Contracts
{
    // P2 的 HTTP 契約（後端 ↔ Unity 面板）。這些是對外的 DTO，不是資料庫 Entity：
    // Entity 可以自由調整欄位與關聯，DTO 一旦被顯示端使用就要考慮相容性。

    /// <summary>批次目前所處的階段。實體位置另外由 StationCode／VehicleId 表示。</summary>
    public enum LotStatus
    {
        /// <summary>在某站等待搬運（已建立搬運任務）。</summary>
        AwaitingTransport,

        /// <summary>已在取料點交接，物料在車上。</summary>
        InTransit,

        /// <summary>已送達加工站，排隊等待加工。</summary>
        Queued,

        Processing,

        /// <summary>P2 流程終點：已送到暫存位置。</summary>
        Staged,
    }

    public enum TransportTaskStatus
    {
        Pending,
        Assigned,
        Executing,
        Completed,
        Failed,
        Cancelled,
    }

    /// <summary>一次派工嘗試的狀態；「等待設備接受」記錄在這裡而不是任務上。</summary>
    public enum AssignmentStatus
    {
        /// <summary>指令已寫入 Outbox，尚未收到設備回覆。</summary>
        Dispatched,

        /// <summary>指令逾時沒有回覆：待確認。仍占用車輛與任務，不會改派給另一台車。</summary>
        Unconfirmed,

        Accepted,
        Loaded,
        Completed,
        Rejected,
        Failed,
    }

    public enum TransportTaskKind
    {
        /// <summary>從倉庫送料到加工站。</summary>
        Replenishment,

        /// <summary>把某站加工完成的批次送往下一站。</summary>
        WipTransfer,
    }

    public enum OperationStatus
    {
        Queued,
        Running,
        Completed,
    }

    public sealed class StationDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>交接點座標（公尺）。</summary>
        public double HandoffX { get; set; }
        public double HandoffZ { get; set; }

        /// <summary>待加工料容量（件）；null 表示這個站的容量尚未建模，不代表無限。</summary>
        public int? InboundCapacity { get; set; }
    }

    public sealed class ProcessStepDto
    {
        public int Sequence { get; set; }
        public string StationCode { get; set; } = string.Empty;
        public string StationName { get; set; } = string.Empty;

        /// <summary>每加工一件需要的時間（秒）。</summary>
        public double SecondsPerPiece { get; set; }

        /// <summary>刀具壽命：加工這麼多件後要換刀。</summary>
        public int ToolLifePieces { get; set; }
    }

    /// <summary>可由面板修改的示範設定；GET 與 PUT /api/settings 共用。</summary>
    public sealed class ProductionSettingsDto
    {
        /// <summary>新批次的預設數量（件）。</summary>
        public int LotQuantity { get; set; }

        /// <summary>唯讀：批量上限，等於流程中最小的待加工料容量。PUT 時忽略。</summary>
        public int MaxLotQuantity { get; set; }

        public string PartCode { get; set; } = string.Empty;
        public string SourceStationCode { get; set; } = string.Empty;
        public string BufferStationCode { get; set; } = string.Empty;

        /// <summary>AGV 上料、下料時每件需要的時間（秒）；一趟的交接時間＝件數 × 這個值。</summary>
        public double HandoverSecondsPerPiece { get; set; }

        /// <summary>機台換刀需要的時間（秒）；換刀期間機台暫停加工。</summary>
        public double ToolChangeSeconds { get; set; }

        /// <summary>PUT 時只採用 Sequence、SecondsPerPiece 與 ToolLifePieces。</summary>
        public List<ProcessStepDto> Steps { get; set; } = new List<ProcessStepDto>();
    }

    public sealed class CreateLotRequest
    {
        /// <summary>省略時使用設定的 LotQuantity。</summary>
        public int? Quantity { get; set; }
    }

    public sealed class LotDto
    {
        public Guid Id { get; set; }
        public string LotNumber { get; set; } = string.Empty;
        public string PartCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public LotStatus Status { get; set; }

        /// <summary>批次所在站點；在車上時為 null。</summary>
        public string? StationCode { get; set; }

        /// <summary>載運批次的車輛；不在車上時為 null。</summary>
        public string? VehicleId { get; set; }

        /// <summary>目前所在或前往的工序；流程走完後為 null。</summary>
        public int? CurrentStepSequence { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public sealed class InventoryMovementDto
    {
        public string? From { get; set; }
        public string To { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTimeOffset OccurredAt { get; set; }
    }

    public sealed class OperationDto
    {
        public int StepSequence { get; set; }
        public string StationCode { get; set; } = string.Empty;
        public OperationStatus Status { get; set; }
        public int Quantity { get; set; }
        public int PiecesCompleted { get; set; }
        public double? SecondsPerPiece { get; set; }
        public DateTimeOffset QueuedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }

    public sealed class TransportTaskDto
    {
        public Guid Id { get; set; }
        public Guid LotId { get; set; }
        public string LotNumber { get; set; } = string.Empty;
        public string FromStationCode { get; set; } = string.Empty;
        public string ToStationCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public TransportTaskKind Kind { get; set; }
        public TransportTaskStatus Status { get; set; }

        /// <summary>最近一次派工的車輛與狀態；尚未派工時為 null。</summary>
        public string? VehicleId { get; set; }
        public AssignmentStatus? AssignmentStatus { get; set; }
        public int Attempts { get; set; }

        public string? FailureReason { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }

    public sealed class TaskEventDto
    {
        public TransportTaskStatus? FromStatus { get; set; }
        public TransportTaskStatus ToStatus { get; set; }
        public string? VehicleId { get; set; }
        public string? Reason { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
    }

    /// <summary>機台正在加工的批次與進度。</summary>
    public sealed class MachiningStatusDto
    {
        public string LotNumber { get; set; } = string.Empty;
        public int PiecesCompleted { get; set; }
        public int Quantity { get; set; }
    }

    public sealed class ToolStatusDto
    {
        /// <summary>上次換刀後已加工的件數。</summary>
        public int PiecesUsed { get; set; }
        public int LifePieces { get; set; }
        public bool Changing { get; set; }

        /// <summary>換刀進度，0～1；沒有在換刀時為 0。</summary>
        public double ChangeProgress { get; set; }
    }

    /// <summary>站點目前的物料與設備狀態（GET /api/stations/status），供顯示端畫進度條。</summary>
    public sealed class StationStatusDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>是否為加工站（有工序）。</summary>
        public bool IsMachine { get; set; }

        /// <summary>備料區：等待加工的件數（排隊中的批次＋加工中批次還沒做的件數）；暫存點則是已暫存的件數。</summary>
        public int InboundQuantity { get; set; }
        public int? InboundCapacity { get; set; }

        /// <summary>成品區：已加工完成、還沒被搬走的件數；倉庫則是等待搬運的件數。</summary>
        public int OutboundQuantity { get; set; }
        public int? OutboundCapacity { get; set; }

        /// <summary>沒有在加工時為 null。</summary>
        public MachiningStatusDto? Machining { get; set; }

        /// <summary>不是加工站時為 null。</summary>
        public ToolStatusDto? Tool { get; set; }
    }

    /// <summary>一個批次的完整追蹤：位置異動、加工紀錄與搬運任務。</summary>
    public sealed class LotTraceDto
    {
        public LotDto Lot { get; set; } = new LotDto();
        public List<InventoryMovementDto> Movements { get; set; } = new List<InventoryMovementDto>();
        public List<OperationDto> Operations { get; set; } = new List<OperationDto>();
        public List<TransportTaskDto> Tasks { get; set; } = new List<TransportTaskDto>();
    }
}
