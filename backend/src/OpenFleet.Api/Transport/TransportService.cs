using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenFleet.Api.Data;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Production;
using OpenFleet.Api.Traffic;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Transport;

/// <summary>
/// 派工與搬運狀態（Scoped）。兩個入口：派工迴圈呼叫 <see cref="DispatchPendingAsync"/>，
/// 收到車輛事件時呼叫 <see cref="ApplyVehicleEventAsync"/>。
/// 後端不會自己判定搬運成功：任務只會因為設備回報而前進。
/// 搬運出問題時的恢復流程：物料還在取料站的可以自動重派；物料可能在車上的要等人工確認（<see cref="ResolveAsync"/>）。
/// </summary>
/// <summary>一台車目前的有效派工：交通控制用它決定車要去取料點還是卸料點。</summary>
public sealed record MovementGoal(string VehicleId, AssignmentStatus Status, string FromStationCode, string ToStationCode);

public sealed class TransportService(
    OpenFleetDbContext db,
    ProductionService production,
    VehicleShortageService shortage,
    RoadNetwork network,
    IOptions<FleetOptions> fleetOptions,
    IOptions<MqttSettings> mqtt,
    TimeProvider time,
    ILogger<TransportService> logger)
{
    /// <summary>
    /// 把等待中的任務派給可用車輛，回傳成功派出的數量。
    /// <paramref name="onlineVehicles"/> 來自記憶體中的即時狀態；能不能派、派給誰則以資料庫為準。
    /// </summary>
    public async Task<int> DispatchPendingAsync(IReadOnlyCollection<VehicleState> onlineVehicles, CancellationToken cancellationToken)
    {
        var pending = (await db.TransportTasks.Include(t => t.Lot)
            .Where(t => t.Status == TransportTaskStatus.Pending)
            .ToListAsync(cancellationToken))
            // 優先順序（使用者 2026-10-09 確認）：搬完成品（含各工序做完的半成品）→ 送刀具 → 補料；同級依建立時間。
            .OrderBy(t => Priority(t.Kind)).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .ToList();
        if (pending.Count == 0)
        {
            await shortage.RecordAsync([], cancellationToken);
            return 0;
        }

        // 先過濾「能不能做」：物料搬運用大型車、刀具配送用小型車，而且要開放派工、連線中、目前沒有有效派工。
        var busy = await db.Assignments
            .Where(a => Assignment.ActiveStatuses.Contains(a.Status))
            .Select(a => a.VehicleId)
            .ToListAsync(cancellationToken);

        // 剛回報「忙碌」的車（還在做後端不記得的工作，例如示範重設前派出的）先休息一下，任務交給別台車。
        var restingSince = time.GetUtcNow() - BusyCooldown;
        busy.AddRange(await db.Assignments
            .Where(a => a.Status == AssignmentStatus.Rejected && a.Reason == VehicleRejectReasons.Busy && a.ClosedAt > restingSince)
            .Select(a => a.VehicleId)
            .Distinct()
            .ToListAsync(cancellationToken));
        var types = await db.Vehicles
            .Where(v => v.DispatchEnabled)
            .ToDictionaryAsync(v => v.Id, v => v.Type, cancellationToken);
        var online = onlineVehicles
            .Where(v => v.Connectivity == VehicleConnectivity.Online && types.ContainsKey(v.VehicleId))
            .ToList();
        var available = online.Where(v => !busy.Contains(v.VehicleId)).ToList();

        var stations = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.Code, cancellationToken);
        // 測試模式下交接時間依設定的倍率縮短；派出的那一刻決定，之後改設定不影響已派出的搬運。
        var handoverSeconds = (await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken)).EffectiveHandoverSeconds;
        var options = fleetOptions.Value;
        var dispatched = 0;
        var waitingForVehicle = new List<WaitingTask>();

        foreach (var task in pending)
        {
            if (task.Kind != TransportTaskKind.ToolDelivery
                && !await HasRoomAsync(stations[task.ToStationCode], task.Quantity, cancellationToken))
            {
                // 目的站放不下：任務繼續等待，不占用車輛，也不影響排在後面、目的地不同的任務。這不算缺車。
                continue;
            }

            var requiredType = VehicleTypeFor(task.Kind);
            var candidates = available.Where(v => types[v.VehicleId] == requiredType).ToList();
            if (candidates.Count == 0)
            {
                // 可以搬、但叫不到車，等待從這裡起算，到派出車為止。
                // 有車在線但都在忙是缺車；這個車型一台都不在線是重大事故，兩者分開記錄。
                var anyOnline = online.Any(v => types[v.VehicleId] == requiredType);
                waitingForVehicle.Add(new WaitingTask(task, anyOnline ? ShortageReason.VehiclesBusy : ShortageReason.FleetOffline));
                continue;
            }

            // 再比較「誰比較合適」：沿路網開到取料點最近的車，距離相同時用 ID 決定，結果可重現。
            var pickup = stations[task.FromStationCode];
            var pickupNode = network.Nearest(pickup.HandoffX, pickup.HandoffZ).Code;
            var vehicle = candidates
                .OrderBy(v => network.PathLength(network.Nearest(v.Position.X, v.Position.Z).Code, pickupNode))
                .ThenBy(v => v.VehicleId, StringComparer.Ordinal)
                .First();

            var now = time.GetUtcNow();
            var attempt = await db.Assignments.CountAsync(a => a.TaskId == task.Id, cancellationToken) + 1;
            var assignment = task.Assign(vehicle.VehicleId, attempt, now, options.CommandTimeToLive);
            db.Assignments.Add(assignment);
            db.OutboxMessages.Add(CreateCommandMessage(assignment, task, pickup, stations[task.ToStationCode], handoverSeconds, now));

            try
            {
                // 派工、任務狀態、歷史與待發送指令在同一筆交易寫入：要嘛全部成功，要嘛全部沒有。
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // 另一個工作者先改了這個任務（xmin 不符），或先占用了這台車（唯一索引）。放棄這一輪，下一輪重新讀取。
                logger.LogWarning("Dispatch of task {TaskId} to {VehicleId} lost a race: {Message}", task.Id, vehicle.VehicleId, ex.InnerException?.Message ?? ex.Message);
                db.ChangeTracker.Clear();
                return dispatched;
            }

            available.Remove(vehicle);
            dispatched++;
            logger.LogInformation("Task {TaskId} ({Kind}, {From} -> {To}, lot {LotNumber}) assigned to {VehicleId}, attempt {Attempt}.",
                task.Id, task.Kind, task.FromStationCode, task.ToStationCode, task.Lot?.LotNumber ?? "-", vehicle.VehicleId, attempt);
        }

        await shortage.RecordAsync(waitingForVehicle, cancellationToken);
        return dispatched;
    }

    /// <summary>每台有有效派工的車輛，以及那筆派工的狀態與起訖站。</summary>
    public async Task<List<MovementGoal>> GetMovementGoalsAsync(CancellationToken cancellationToken) =>
        await db.Assignments.AsNoTracking()
            .Where(a => Assignment.ActiveStatuses.Contains(a.Status))
            .Select(a => new MovementGoal(a.VehicleId, a.Status, a.Task.FromStationCode, a.Task.ToStationCode))
            .ToListAsync(cancellationToken);

    /// <summary>目前有有效派工（還沒結束）的車輛。</summary>
    public async Task<IReadOnlyCollection<string>> GetBusyVehicleIdsAsync(CancellationToken cancellationToken) =>
        await db.Assignments
            .Where(a => Assignment.ActiveStatuses.Contains(a.Status))
            .Select(a => a.VehicleId)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// 指令逾時還沒有任何回覆：標記為待確認。派工仍然有效（占用車輛與任務），
    /// 因為設備可能已經在執行，不能直接把任務交給另一台車。
    /// </summary>
    public async Task<int> MarkExpiredAssignmentsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var expired = await db.Assignments
            .Where(a => a.Status == AssignmentStatus.Dispatched && a.ExpiresAt < now)
            .ToListAsync(cancellationToken);

        foreach (var assignment in expired)
        {
            assignment.MarkUnconfirmed();
            logger.LogWarning("Assignment {AssignmentId} on {VehicleId} got no reply before {ExpiresAt}; marked unconfirmed.",
                assignment.Id, assignment.VehicleId, assignment.ExpiresAt);
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return expired.Count;
    }

    /// <summary>
    /// 套用設備對某筆派工的回報。呼叫端負責交易與去重（Inbox）；這裡只檢查轉移是否合法，
    /// 不合法（例如遲到的回報、不是這台車的派工）就記錄並忽略。
    /// </summary>
    public async Task ApplyVehicleEventAsync(string vehicleId, VehicleEvent vehicleEvent, CancellationToken cancellationToken)
    {
        var assignment = await db.Assignments
            .Include(a => a.Task).ThenInclude(t => t.Lot)
            .SingleOrDefaultAsync(a => a.Id == vehicleEvent.AssignmentId, cancellationToken);

        if (assignment is null || assignment.VehicleId != vehicleId)
        {
            logger.LogWarning("Event {EventType} from {VehicleId} refers to unknown assignment {AssignmentId}; ignored.",
                vehicleEvent.EventType, vehicleId, vehicleEvent.AssignmentId);
            return;
        }

        var task = assignment.Task;
        // 送刀具的任務沒有批次。
        var lot = task.Lot;
        var now = time.GetUtcNow();

        switch (vehicleEvent.EventType)
        {
            case VehicleEventType.Accepted when assignment.IsAwaitingReply:
                Accept();
                break;

            case VehicleEventType.Rejected when assignment.IsAwaitingReply && vehicleEvent.Reason == VehicleRejectReasons.Busy:
                // 車還在做別的事（例如示範重設前派出的工作）：車本身沒問題，不算一次失敗。任務回到待派工，這台車先休息 BusyCooldown。
                assignment.Close(AssignmentStatus.Rejected, vehicleEvent.Reason, now);
                task.Requeue(assignment, vehicleEvent.Reason, now);
                break;

            case VehicleEventType.Rejected when assignment.IsAwaitingReply:
                assignment.Close(AssignmentStatus.Rejected, vehicleEvent.Reason, now);
                var failures = await FailedAttemptsAsync(task.Id, cancellationToken) + 1;
                if (failures >= fleetOptions.Value.MaxDispatchAttempts)
                {
                    task.Fail(assignment, $"Rejected {failures} time(s): {vehicleEvent.Reason}", now);
                }
                else
                {
                    task.Requeue(assignment, vehicleEvent.Reason, now);
                }

                break;

            case VehicleEventType.Loaded when assignment.IsAwaitingReply || assignment.Status == AssignmentStatus.Accepted:
                if (assignment.IsAwaitingReply)
                {
                    // Accepted 遺失但設備確實在執行：補上接受，再處理取料。
                    Accept();
                }

                assignment.MarkLoaded(now);
                if (lot is not null)
                {
                    var source = lot.Location;
                    lot.LoadOnto(vehicleId, now);
                    db.InventoryMovements.Add(InventoryMovement.Record(lot, source, "Loaded", now));
                }

                break;

            case VehicleEventType.Completed when assignment.Status == AssignmentStatus.Loaded:
                assignment.Close(AssignmentStatus.Completed, null, now);
                task.Complete(assignment, now);
                if (lot is not null)
                {
                    var vehicleLocation = lot.Location;
                    lot.UnloadAt(task.ToStationCode, now);
                    db.InventoryMovements.Add(InventoryMovement.Record(lot, vehicleLocation, "Unloaded", now));
                    production.OnLotArrived(lot, now);
                }
                else
                {
                    // 刀具配送完成：新刀在機邊備用。換刀是機台端的另一件事，另外記錄。
                    await production.OnToolDeliveredAsync(task.ToStationCode, now, cancellationToken);
                }

                break;

            case VehicleEventType.Failed when assignment.IsActive:
                await FailAssignmentAsync(assignment, vehicleEvent.Reason, now, cancellationToken);
                break;

            default:
                logger.LogWarning("Event {EventType} is not valid for assignment {AssignmentId} in {Status}; ignored.",
                    vehicleEvent.EventType, assignment.Id, assignment.Status);
                return;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Assignment {AssignmentId} on {VehicleId}: {EventType} -> {Kind} task {TaskStatus}, lot {LotNumber} {LotStatus} at {Location}.",
            assignment.Id, vehicleId, vehicleEvent.EventType, task.Kind, task.Status, lot?.LotNumber ?? "-", lot?.Status, lot?.Location ?? task.ToStationCode);

        void Accept()
        {
            assignment.Accept(now);
            task.Start(assignment, now);
        }
    }

    /// <summary>車輛回報「忙碌」後，這台車多久不派新工作。</summary>
    public static readonly TimeSpan BusyCooldown = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 這個任務之前真正失敗過幾次（被拒絕或執行失敗）。回報「忙碌」的不算：車沒有問題，只是當時沒空。
    /// 還沒存檔的這一次由呼叫端自己加上。
    /// </summary>
    private Task<int> FailedAttemptsAsync(Guid taskId, CancellationToken cancellationToken) =>
        db.Assignments.CountAsync(a => a.TaskId == taskId
            && (a.Status == AssignmentStatus.Rejected || a.Status == AssignmentStatus.Failed)
            && (a.Reason == null || a.Reason != VehicleRejectReasons.Busy), cancellationToken);

    /// <summary>
    /// 一趟搬運失敗之後怎麼辦，依「物料在哪裡」決定：
    /// 還沒取料 → 物料還在取料站，任務回到等待中重新派工（次數用完才標記失敗）。
    /// 已經取料 → 物料在車上，系統無法確認實際狀況，標記失敗等人工處理。
    /// </summary>
    private async Task FailAssignmentAsync(Assignment assignment, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var task = assignment.Task;
        var loaded = assignment.IsLoaded;
        assignment.Close(AssignmentStatus.Failed, reason, now);

        if (!loaded && await FailedAttemptsAsync(task.Id, cancellationToken) + 1 < fleetOptions.Value.MaxDispatchAttempts)
        {
            task.Requeue(assignment, reason, now);
            logger.LogWarning("Assignment {AssignmentId} on {VehicleId} failed before pickup ({Reason}); task {TaskId} will be dispatched again.",
                assignment.Id, assignment.VehicleId, reason, task.Id);
        }
        else
        {
            task.Fail(assignment, loaded ? $"Failed with the load on {assignment.VehicleId}: {reason}" : $"Failed before pickup: {reason}", now);
            logger.LogError("Task {TaskId} ({From} -> {To}) failed on {VehicleId} and needs attention: {Reason}.",
                task.Id, task.FromStationCode, task.ToStationCode, assignment.VehicleId, task.FailureReason);
        }
    }

    /// <summary>
    /// 對帳：後端認為還在進行的派工，設備是否真的在執行。車輛在線、指令的有效期限已過，
    /// 卻持續 <see cref="FleetOptions.LostCommandGrace"/> 都回報沒有這道指令，就當作這趟搬運失敗（例如設備重啟、指令遺失）。
    /// 車輛離線時無法判斷，維持原狀；那種情況只能人工處理。回傳這一輪認定失敗的數量。
    /// </summary>
    public async Task<int> ReconcileAsync(IReadOnlyCollection<VehicleState> vehicles, CancellationToken cancellationToken)
    {
        var active = await db.Assignments.Include(a => a.Task)
            .Where(a => Assignment.ActiveStatuses.Contains(a.Status))
            .ToListAsync(cancellationToken);
        if (active.Count == 0)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var states = vehicles.ToDictionary(v => v.VehicleId, StringComparer.Ordinal);
        var lost = 0;

        foreach (var assignment in active)
        {
            if (!states.TryGetValue(assignment.VehicleId, out var vehicle)
                || vehicle.Connectivity != VehicleConnectivity.Online
                || now <= assignment.ExpiresAt)
            {
                continue;
            }

            if (vehicle.ActiveCommandId == assignment.CommandId)
            {
                assignment.MarkPresent();
                continue;
            }

            assignment.MarkMissing(now);
            if (now - assignment.MissingSince >= fleetOptions.Value.LostCommandGrace)
            {
                await FailAssignmentAsync(assignment, "Vehicle no longer reports this command", now, cancellationToken);
                lost++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return lost;
    }

    /// <summary>
    /// 人工處理失敗的搬運：有人到現場確認之後，告訴系統物料（或刀具）放回了取料站，還是已經送到卸料站。
    /// 任務還在進行、但車輛離線而無法對帳時也可以用：先把那筆派工結束，再照同樣的方式處理。
    /// </summary>
    public async Task ResolveAsync(
        Guid taskId, TaskResolution resolution, IReadOnlyCollection<VehicleState> vehicles, CancellationToken cancellationToken)
    {
        var task = await db.TransportTasks
            .Include(t => t.Lot)
            .Include(t => t.Assignments)
            .SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new RequestValidationException("id", "Task does not exist.");
        var now = time.GetUtcNow();

        if (task.Assignments.SingleOrDefault(a => a.IsActive) is { } active)
        {
            var online = vehicles.Any(v => v.VehicleId == active.VehicleId && v.Connectivity == VehicleConnectivity.Online);
            if (online)
            {
                throw new RequestValidationException("id", $"Task is still being carried out by {active.VehicleId}, which is online.");
            }

            active.Close(AssignmentStatus.Failed, "Closed by operator while the vehicle was offline", now);
            task.Fail(active, $"Vehicle {active.VehicleId} went offline during the task", now);
        }

        if (!task.NeedsAttention)
        {
            throw new RequestValidationException("id", $"Task is {task.Status} and does not need to be resolved.");
        }

        task.Resolve(resolution, now);
        var lot = task.Lot;
        var from = lot?.Location;

        if (resolution == TaskResolution.ReturnToSource)
        {
            // 放回取料站，另開一趟新的搬運；這一筆任務留著失敗的紀錄。
            if (lot is not null)
            {
                lot.ReturnTo(task.FromStationCode, now);
                db.InventoryMovements.Add(InventoryMovement.Record(lot, from, "ManualReturn", now));
                db.TransportTasks.Add(TransportTask.Create(lot, task.FromStationCode, task.ToStationCode, task.Kind, now));
            }
            else
            {
                db.TransportTasks.Add(TransportTask.CreateToolDelivery(task.FromStationCode, task.ToStationCode, now));
            }
        }
        else if (lot is not null)
        {
            lot.PlaceAt(task.ToStationCode, now);
            db.InventoryMovements.Add(InventoryMovement.Record(lot, from, "ManualDelivery", now));
            production.OnLotArrived(lot, now);
        }
        else
        {
            await production.OnToolDeliveredAsync(task.ToStationCode, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Task {TaskId} ({From} -> {To}) resolved by operator: {Resolution}.", task.Id, task.FromStationCode, task.ToStationCode, resolution);
    }

    /// <summary>
    /// 目的站的待加工料容量夠不夠。已承諾量＝備料區現有的數量（排隊中的批次＋加工中批次還沒做的件數）
    /// ＋已派工、還沒卸料的數量；每一筆只算一次（卸料後任務完成，數量就改算在站內）。
    /// </summary>
    private async Task<bool> HasRoomAsync(Station destination, int quantity, CancellationToken cancellationToken)
    {
        if (destination.InboundCapacity is not { } capacity)
        {
            return true;
        }

        var waiting = await db.Lots
            .Where(l => l.StationCode == destination.Code && l.Status == LotStatus.Queued)
            .SumAsync(l => l.Quantity, cancellationToken);
        var remaining = await db.Operations
            .Where(o => o.StationCode == destination.Code && o.Status == OperationStatus.Running)
            .SumAsync(o => o.Quantity - o.PiecesCompleted, cancellationToken);
        var inbound = await db.TransportTasks
            .Where(t => t.ToStationCode == destination.Code
                && t.Kind != TransportTaskKind.ToolDelivery
                && (t.Status == TransportTaskStatus.Assigned || t.Status == TransportTaskStatus.Executing))
            .SumAsync(t => t.Quantity, cancellationToken);

        // 容量以支計；料框比容量大的桿徑（24 mm 一框 45 支）至少放得下一框，否則永遠送不進去。
        return waiting + remaining + inbound + quantity <= Math.Max(capacity, quantity);
    }

    private OutboxMessage CreateCommandMessage(
        Assignment assignment, TransportTask task, Station pickup, Station destination, double handoverSeconds, DateTimeOffset now)
    {
        var command = new VehicleCommand
        {
            CommandId = assignment.CommandId,
            AssignmentId = assignment.Id,
            CommandType = VehicleCommandType.Transport,
            ExpiresAt = assignment.ExpiresAt,
            Pickup = new Position3 { X = pickup.HandoffX, Z = pickup.HandoffZ },
            Destination = new Position3 { X = destination.HandoffX, Z = destination.HandoffZ },
            // 件數與內容讓設備決定載貨時的車速。交接是一框一框整個交給站點，時間固定、和件數無關，取料與卸料各一次。
            LoadQuantity = task.Quantity,
            LoadCode = task.Lot?.PartCode ?? LoadCodes.Tool,
            HandoverSeconds = handoverSeconds,
        };
        var envelope = MessageEnvelope.Create(mqtt.Value.ProducerId, command, now);

        return new OutboxMessage
        {
            MessageId = envelope.MessageId,
            Topic = Topics.Vehicle(mqtt.Value.FactoryId, assignment.VehicleId, VehicleChannel.Commands),
            Payload = JsonSerializer.Serialize(envelope, JsonDefaults.Options),
            CreatedAt = now,
        };
    }

    private static int Priority(TransportTaskKind kind) => kind switch
    {
        TransportTaskKind.WipTransfer => 0,
        TransportTaskKind.ToolDelivery => 1,
        _ => 2,
    };

    /// <summary>小型車只送刀具；物料搬運只用大型車。</summary>
    private static VehicleType VehicleTypeFor(TransportTaskKind kind) =>
        kind == TransportTaskKind.ToolDelivery ? VehicleType.Small : VehicleType.Large;
}
