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

        /// <summary>小型車把新刀具從刀具庫送到機邊備用。沒有批次；換刀由機台端的設備執行，不是這趟搬運的一部分。</summary>
        ToolDelivery,
    }

    /// <summary>搬運指令裡 LoadCode 的固定值。</summary>
    public static class LoadCodes
    {
        /// <summary>車上載的是刀具，不是零件。</summary>
        public const string Tool = "tool";
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

        /// <summary>
        /// 機台取放一件的時間（秒）：加工前從備料區取一件、加工後取出放到成品區，各一次。
        /// 所以每件的週期＝這個值 × 2 ＋ 該站的加工時間。
        /// </summary>
        public double PieceHandlingSeconds { get; set; }

        /// <summary>AGV 與站點交接一框的時間（秒）：把一框料交給站點、或從站點取走一框，各一次，和件數無關。</summary>
        public double AgvHandoverSeconds { get; set; }

        /// <summary>機台換刀需要的時間（秒）；換刀期間機台暫停加工。</summary>
        public double ToolChangeSeconds { get; set; }

        /// <summary>刀具剩餘可加工件數低於（或等於）這個值時，預先叫小型車送一把新刀到機邊。</summary>
        public int ToolRequestThresholdPieces { get; set; }

        /// <summary>
        /// 是否由小型車配送刀具。關閉時視為新刀永遠在機邊，到壽命就直接換（P2 的行為，測試與除錯用）。
        /// </summary>
        public bool ToolDeliveryEnabled { get; set; } = true;

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

        /// <summary>搬運的批次；送刀具的任務沒有批次，為 null。</summary>
        public Guid? LotId { get; set; }
        public string? LotNumber { get; set; }
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

        /// <summary>備用的新刀：還沒叫、已叫車配送中、已送到機邊。</summary>
        public ToolSpareStatus Spare { get; set; }

        /// <summary>刀具已到壽命，但新刀還沒送到：機台停機等刀。</summary>
        public bool WaitingForTool { get; set; }
    }

    public enum ToolSpareStatus
    {
        None,

        /// <summary>已建立配送任務，新刀還沒到。</summary>
        Requested,

        /// <summary>新刀已送到機邊；到壽命時可以直接換刀。</summary>
        Delivered,
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

        /// <summary>機台目前因為叫不到車而停下來的原因；沒有時為 null。</summary>
        public MachineDelayCause? ShortageCause { get; set; }

        /// <summary>叫不到車是因為車都在忙（缺車）還是全部離線（重大事故）；沒有時為 null。</summary>
        public ShortageReason? ShortageReason { get; set; }
    }

    /// <summary>叫不到車的原因。兩者分開記錄、分開統計。</summary>
    public enum ShortageReason
    {
        /// <summary>缺車：有車在線，但都在忙。代表車不夠用，不算緊急事件。</summary>
        VehiclesBusy,

        /// <summary>重大事故：能做這個任務的車型全部離線。</summary>
        FleetOffline,
    }

    /// <summary>機台因為叫不到車而停下來的原因。</summary>
    public enum MachineDelayCause
    {
        /// <summary>等不到料：要送來的料叫不到車，機台沒有東西可做。</summary>
        WaitingForMaterial,

        /// <summary>成品沒被搬走：做完的批次叫不到車，下一批無法開始。</summary>
        OutputNotRemoved,

        /// <summary>等刀：刀具已到壽命，送新刀的任務叫不到車，機台停機。</summary>
        WaitingForTool,
    }

    /// <summary>某道工序因缺車耽誤的加工時間。秒數都是模擬時間（1 倍速時等於實際時間）。</summary>
    public sealed class StepDelayDto
    {
        public int Sequence { get; set; }
        public string StationCode { get; set; } = string.Empty;
        public string StationName { get; set; } = string.Empty;

        /// <summary>因缺車（車都在忙）耽誤的總時間＝下面三項相加。不含離線事故。</summary>
        public double DelaySeconds { get; set; }
        public double WaitingForMaterialSeconds { get; set; }
        public double OutputNotRemovedSeconds { get; set; }
        public double WaitingForToolSeconds { get; set; }

        /// <summary>因車輛全部離線（重大事故）而停機的時間，不算在缺車裡。</summary>
        public double OfflineSeconds { get; set; }

        /// <summary>機台此刻因為叫不到車停著的原因；沒有時為 null。</summary>
        public MachineDelayCause? CurrentCause { get; set; }
        public ShortageReason? CurrentReason { get; set; }
    }

    /// <summary>
    /// 缺車統計（GET /api/shortage）。缺車（車都在忙）與離線事故（該車型全部離線）分開：
    /// 前三個欄位與各工序的 DelaySeconds 只算缺車。
    /// </summary>
    public sealed class VehicleShortageDto
    {
        /// <summary>此刻因為車都在忙而叫不到車的搬運任務數。</summary>
        public int WaitingTasks { get; set; }

        /// <summary>累計的缺車等待次數（一個任務可能等待不只一次）。</summary>
        public int WaitCount { get; set; }

        /// <summary>任務等車的累計時間（模擬秒）。這是任務的等待，不等於機台的耽誤。</summary>
        public double WaitSeconds { get; set; }

        /// <summary>重大事故：此刻全部離線的車型（有開放派工的車型裡，沒有任何一台在線）。空的表示沒有事故。</summary>
        public List<VehicleType> OfflineVehicleTypes { get; set; } = new List<VehicleType>();

        /// <summary>此刻因為車輛全部離線而派不出去的任務數。</summary>
        public int OfflineWaitingTasks { get; set; }
        public int OfflineWaitCount { get; set; }

        /// <summary>任務因離線事故等待的累計時間（模擬秒）。</summary>
        public double OfflineWaitSeconds { get; set; }

        /// <summary>各工序機台真的停下來的時間。</summary>
        public List<StepDelayDto> Steps { get; set; } = new List<StepDelayDto>();
    }

    /// <summary>一筆缺車等待區間（GET /api/shortage/waits）。</summary>
    public sealed class VehicleWaitDto
    {
        public Guid TaskId { get; set; }
        public ShortageReason Reason { get; set; }

        /// <summary>送刀具的任務沒有批次，為 null。</summary>
        public string? LotNumber { get; set; }
        public TransportTaskKind Kind { get; set; }
        public string FromStationCode { get; set; } = string.Empty;
        public string ToStationCode { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }

        /// <summary>還在等待時為 null。</summary>
        public DateTimeOffset? EndedAt { get; set; }
        public double SimulatedSeconds { get; set; }
    }

    /// <summary>一筆機台耽誤區間（GET /api/shortage/delays）。</summary>
    public sealed class MachineDelayDto
    {
        public string StationCode { get; set; } = string.Empty;
        public int StepSequence { get; set; }
        public MachineDelayCause Cause { get; set; }
        public ShortageReason Reason { get; set; }
        public Guid TaskId { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public double SimulatedSeconds { get; set; }
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
