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
        var types = await db.Vehicles
            .Where(v => v.DispatchEnabled)
            .ToDictionaryAsync(v => v.Id, v => v.Type, cancellationToken);
        var online = onlineVehicles
            .Where(v => v.Connectivity == VehicleConnectivity.Online && types.ContainsKey(v.VehicleId))
            .ToList();
        var available = online.Where(v => !busy.Contains(v.VehicleId)).ToList();

        var stations = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.Code, cancellationToken);
        var handoverSeconds = await db.ProductionSettings.AsNoTracking()
            .Select(s => s.AgvHandoverSeconds)
            .SingleAsync(cancellationToken);
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

            case VehicleEventType.Rejected when assignment.IsAwaitingReply:
                assignment.Close(AssignmentStatus.Rejected, vehicleEvent.Reason, now);
                if (assignment.Attempt >= fleetOptions.Value.MaxDispatchAttempts)
                {
                    task.Fail(assignment, $"Rejected {assignment.Attempt} time(s): {vehicleEvent.Reason}", now);
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
                // 物料（或刀具）停在失敗當下的位置（原站或車上），不自動重派；恢復流程留到之後的階段。
                assignment.Close(AssignmentStatus.Failed, vehicleEvent.Reason, now);
                task.Fail(assignment, vehicleEvent.Reason, now);
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

        return waiting + remaining + inbound + quantity <= capacity;
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
