using OpenFleet.Contracts;

namespace OpenFleet.Api.Data;

/// <summary>車輛主檔。位置與連線狀態是高頻資料，仍放在記憶體的 FleetStateStore，不寫進資料庫。</summary>
public sealed class Vehicle
{
    public required string Id { get; set; }
    public VehicleType Type { get; set; }

    /// <summary>是否參與自動派工。大型車接物料搬運，小型車接刀具配送。</summary>
    public bool DispatchEnabled { get; set; }

    /// <summary>車位座標（交接點，由佈局推導）。不在佈局裡的車沒有車位。</summary>
    public double? HomeX { get; set; }
    public double? HomeZ { get; set; }

    /// <summary>是否在使用中的佈局裡。不在的車保留主檔（歷史紀錄可能參照），但不派工、不接受狀態。</summary>
    public bool InLayout { get; set; }
}

/// <summary>路網節點（座標，公尺）。站點交接點與充電車位也是節點。</summary>
public sealed class RouteNode
{
    public required string Code { get; set; }
    public double X { get; set; }
    public double Z { get; set; }
}

/// <summary>
/// 路網的路段。車道不限方向，所以一筆代表雙向都能走的一段；同一時間誰能用由交通控制的預約決定。
/// </summary>
public sealed class RouteEdge
{
    public int Id { get; set; }
    public required string FromCode { get; set; }
    public required string ToCode { get; set; }
}

/// <summary>
/// 一趟單車搬運：物料批次，或一把刀具（<see cref="TransportTaskKind.ToolDelivery"/>，沒有批次）。合法的狀態轉移：Pending → Assigned → Executing → Completed；
/// Assigned／Executing 可以 Failed；物料還在取料站時（設備拒絕、還沒取料就失敗）回到 Pending 等待重新派工。
/// Failed 之後由人工確認現場狀況再決定怎麼處理（<see cref="Resolve"/>）。
/// 每次轉移都留下一筆 <see cref="TaskEvent"/>。
/// </summary>
public sealed class TransportTask
{
    public Guid Id { get; private set; }
    /// <summary>搬運的批次；送刀具的任務為 null。</summary>
    public Guid? LotId { get; private set; }
    public MaterialLot? Lot { get; private set; }
    public string FromStationCode { get; private set; } = string.Empty;

    /// <summary>
    /// 目的站。送到指定站的（成品倉、出貨、刀具庫、送到機台所在的站）一開始就有；送到某道工序的，
    /// 在待派工時是 null，派車時才從那道工序的站裡挑（<see cref="BindDestination"/>）。
    /// </summary>
    public string? ToStationCode { get; private set; }

    /// <summary>送到某道工序的搬運（P7 第 1a 步）：要做的工序；送到指定站的是 null。</summary>
    public int? ToStepSequence { get; private set; }

    /// <summary>送刀具的任務：新刀是給這一站的哪一台機台（同一站的兩台車床各有自己的刀）；物料搬運是 null。</summary>
    public string? MachineCode { get; private set; }
    public int Quantity { get; private set; }
    public TransportTaskKind Kind { get; private set; }
    public TransportTaskStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>失敗後的人工處理結果與時間；還沒處理時為 null。</summary>
    public TaskResolution? Resolution { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public uint Version { get; private set; }

    /// <summary>失敗而且還沒有人處理。</summary>
    public bool NeedsAttention => Status == TransportTaskStatus.Failed && Resolution is null;

    public List<Assignment> Assignments { get; } = [];
    public List<TaskEvent> Events { get; } = [];

    public static TransportTask Create(MaterialLot lot, string from, string to, TransportTaskKind kind, DateTimeOffset now)
    {
        var task = new TransportTask
        {
            Id = Guid.NewGuid(),
            LotId = lot.Id,
            Lot = lot,
            FromStationCode = from,
            ToStationCode = to,
            Quantity = lot.Quantity,
            Kind = kind,
            Status = TransportTaskStatus.Pending,
            CreatedAt = now,
        };
        task.Events.Add(new TaskEvent { ToStatus = TransportTaskStatus.Pending, OccurredAt = now });
        return task;
    }

    /// <summary>
    /// 送到某道工序的搬運：目的站還沒定，派車時才從這道工序的站裡挑（<see cref="BindDestination"/>）。
    /// 框的目的工序是 <paramref name="stepSequence"/>；搬運的 <see cref="Quantity"/> 與批次同 <see cref="Create"/>。
    /// </summary>
    public static TransportTask CreateToStep(MaterialLot lot, string from, int stepSequence, TransportTaskKind kind, DateTimeOffset now)
    {
        var task = new TransportTask
        {
            Id = Guid.NewGuid(),
            LotId = lot.Id,
            Lot = lot,
            FromStationCode = from,
            ToStationCode = null,
            ToStepSequence = stepSequence,
            Quantity = lot.Quantity,
            Kind = kind,
            Status = TransportTaskStatus.Pending,
            CreatedAt = now,
        };
        task.Events.Add(new TaskEvent { ToStatus = TransportTaskStatus.Pending, OccurredAt = now });
        return task;
    }

    /// <summary>
    /// 派車時決定目的站（只能用在送到工序、待派工、還沒有目的站的任務）。之後派工與搬運都用這一站。
    /// </summary>
    public void BindDestination(string stationCode)
    {
        if (Status != TransportTaskStatus.Pending || ToStationCode is not null || ToStepSequence is null)
        {
            throw new InvalidOperationException(
                $"Task {Id} cannot bind a destination in {Status} (station {ToStationCode ?? "-"}, step {ToStepSequence?.ToString() ?? "-"}).");
        }

        ToStationCode = stationCode;
    }

    /// <summary>把一把新刀從刀具庫送到機邊：送到 <paramref name="to"/> 這一站，給 <paramref name="machineCode"/> 這台機台。</summary>
    public static TransportTask CreateToolDelivery(string from, string to, string machineCode, DateTimeOffset now)
    {
        var task = new TransportTask
        {
            Id = Guid.NewGuid(),
            FromStationCode = from,
            ToStationCode = to,
            MachineCode = machineCode,
            Quantity = 1,
            Kind = TransportTaskKind.ToolDelivery,
            Status = TransportTaskStatus.Pending,
            CreatedAt = now,
        };
        task.Events.Add(new TaskEvent { ToStatus = TransportTaskStatus.Pending, OccurredAt = now });
        return task;
    }

    /// <summary>建立一筆派工嘗試。<paramref name="attempt"/> 由呼叫端依既有派工數決定。</summary>
    public Assignment Assign(string vehicleId, int attempt, DateTimeOffset now, TimeSpan commandTimeToLive)
    {
        var assignment = new Assignment
        {
            TaskId = Id,
            Task = this,
            VehicleId = vehicleId,
            Attempt = attempt,
            AssignedAt = now,
            ExpiresAt = now + commandTimeToLive,
        };
        Assignments.Add(assignment);
        Transition(TransportTaskStatus.Assigned, now, assignment, null, TransportTaskStatus.Pending);
        return assignment;
    }

    public void Start(Assignment assignment, DateTimeOffset now) =>
        Transition(TransportTaskStatus.Executing, now, assignment, null, TransportTaskStatus.Assigned);

    public void Complete(Assignment assignment, DateTimeOffset now)
    {
        Transition(TransportTaskStatus.Completed, now, assignment, null, TransportTaskStatus.Executing);
        CompletedAt = now;
    }

    /// <summary>
    /// 設備沒有接受指令（拒絕或過期），或還沒取料就失敗：物料還在原地，可以重新派工。
    /// 送到工序的任務要把目的站清掉：重新派工時再從那道工序的站裡挑（目的站是派車時才定的）。
    /// </summary>
    public void Requeue(Assignment assignment, string? reason, DateTimeOffset now)
    {
        Transition(TransportTaskStatus.Pending, now, assignment, reason, TransportTaskStatus.Assigned, TransportTaskStatus.Executing);
        if (ToStepSequence is not null)
        {
            ToStationCode = null;
        }
    }

    /// <summary>記錄人工處理的結果。物料的移動與後續的任務由呼叫端處理。</summary>
    public void Resolve(TaskResolution resolution, DateTimeOffset now)
    {
        if (!NeedsAttention)
        {
            throw new InvalidOperationException($"Task {Id} is {Status} and does not need to be resolved.");
        }

        Resolution = resolution;
        ResolvedAt = now;
    }

    public void Fail(Assignment assignment, string? reason, DateTimeOffset now)
    {
        Transition(TransportTaskStatus.Failed, now, assignment, reason, TransportTaskStatus.Assigned, TransportTaskStatus.Executing);
        FailureReason = reason;
        CompletedAt = now;
    }

    private void Transition(TransportTaskStatus to, DateTimeOffset now, Assignment? assignment, string? reason, params TransportTaskStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
        {
            throw new InvalidOperationException($"Task {Id} cannot go from {Status} to {to}.");
        }

        Events.Add(new TaskEvent
        {
            TaskId = Id,
            Assignment = assignment,
            VehicleId = assignment?.VehicleId,
            FromStatus = Status,
            ToStatus = to,
            Reason = reason,
            OccurredAt = now,
        });
        Status = to;
    }
}

/// <summary>
/// 一次派工嘗試：把某個任務交給某台車。任務可以有多次嘗試，但同一時間只有一筆有效；
/// 一台車同一時間也只有一筆有效派工。這兩條規則由資料庫的部分唯一索引保證，不只靠程式檢查。
/// </summary>
public sealed class Assignment
{
    /// <summary>仍占用車輛與任務的狀態。修改時要同步更新 DbContext 裡索引的 filter。</summary>
    public static readonly AssignmentStatus[] ActiveStatuses =
    [
        AssignmentStatus.Dispatched,
        AssignmentStatus.Unconfirmed,
        AssignmentStatus.Accepted,
        AssignmentStatus.Loaded,
    ];

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public TransportTask Task { get; set; } = null!;
    public required string VehicleId { get; set; }
    public int Attempt { get; set; }
    public AssignmentStatus Status { get; private set; } = AssignmentStatus.Dispatched;

    /// <summary>送給設備的指令 ID；設備以此判斷重送。</summary>
    public Guid CommandId { get; set; } = Guid.NewGuid();
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? LoadedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public string? Reason { get; private set; }

    /// <summary>對帳用：從這個時間起，在線的車輛一直回報沒有在執行這道指令；設備有在執行時為 null。</summary>
    public DateTimeOffset? MissingSince { get; private set; }
    public uint Version { get; private set; }

    public bool IsActive => ActiveStatuses.Contains(Status);

    /// <summary>物料已經在車上（已回報取料）。</summary>
    public bool IsLoaded => Status == AssignmentStatus.Loaded;

    public void MarkMissing(DateTimeOffset now) => MissingSince ??= now;

    public void MarkPresent() => MissingSince = null;

    /// <summary>還沒收到設備任何回覆。</summary>
    public bool IsAwaitingReply => Status is AssignmentStatus.Dispatched or AssignmentStatus.Unconfirmed;

    public void MarkUnconfirmed() => Status = AssignmentStatus.Unconfirmed;

    public void Accept(DateTimeOffset now)
    {
        Status = AssignmentStatus.Accepted;
        AcceptedAt = now;
    }

    public void MarkLoaded(DateTimeOffset now)
    {
        Status = AssignmentStatus.Loaded;
        LoadedAt = now;
    }

    public void Close(AssignmentStatus status, string? reason, DateTimeOffset now)
    {
        Status = status;
        Reason = reason;
        ClosedAt = now;
    }
}

/// <summary>任務狀態歷史（只新增、不修改）。</summary>
public sealed class TaskEvent
{
    public long Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid? AssignmentId { get; set; }
    public Assignment? Assignment { get; set; }
    public string? VehicleId { get; set; }
    public TransportTaskStatus? FromStatus { get; set; }
    public TransportTaskStatus ToStatus { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
