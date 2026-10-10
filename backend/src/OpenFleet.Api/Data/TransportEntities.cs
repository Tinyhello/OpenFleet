using OpenFleet.Contracts;

namespace OpenFleet.Api.Data;

/// <summary>車輛主檔。位置與連線狀態是高頻資料，仍放在記憶體的 FleetStateStore，不寫進資料庫。</summary>
public sealed class Vehicle
{
    public required string Id { get; set; }
    public VehicleType Type { get; set; }

    /// <summary>是否參與自動派工。大型車接物料搬運，小型車接刀具配送。</summary>
    public bool DispatchEnabled { get; set; }
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
/// Assigned／Executing 可以 Failed；設備拒絕時 Assigned 回到 Pending 等待重新派工。
/// 每次轉移都留下一筆 <see cref="TaskEvent"/>。
/// </summary>
public sealed class TransportTask
{
    public Guid Id { get; private set; }
    /// <summary>搬運的批次；送刀具的任務為 null。</summary>
    public Guid? LotId { get; private set; }
    public MaterialLot? Lot { get; private set; }
    public string FromStationCode { get; private set; } = string.Empty;
    public string ToStationCode { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public TransportTaskKind Kind { get; private set; }
    public TransportTaskStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public uint Version { get; private set; }

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

    /// <summary>把一把新刀從刀具庫送到機邊。</summary>
    public static TransportTask CreateToolDelivery(string from, string to, DateTimeOffset now)
    {
        var task = new TransportTask
        {
            Id = Guid.NewGuid(),
            FromStationCode = from,
            ToStationCode = to,
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

    /// <summary>設備沒有接受指令（拒絕或過期）：物料還在原地，可以重新派工。</summary>
    public void Requeue(Assignment assignment, string? reason, DateTimeOffset now) =>
        Transition(TransportTaskStatus.Pending, now, assignment, reason, TransportTaskStatus.Assigned);

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
    public uint Version { get; private set; }

    public bool IsActive => ActiveStatuses.Contains(Status);

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
