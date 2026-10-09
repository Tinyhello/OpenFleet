using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Production;

/// <summary>輸入不符合規則；端點會轉成 HTTP 400（ValidationProblem）。</summary>
public sealed class RequestValidationException(string field, string message) : Exception(message)
{
    public IDictionary<string, string[]> Errors { get; } = new Dictionary<string, string[]> { [field] = [message] };
}

/// <summary>
/// 批次、製程設定與加工進度（Scoped：跟著一個 DbContext）。
/// P2 的加工是後端內的簡化模擬：單機、先到先做、整批固定時間、完成後整批放行。
/// </summary>
public sealed class ProductionService(OpenFleetDbContext db, TimeProvider time, ILogger<ProductionService> logger)
{
    public const int MaxDurationSeconds = 3600;

    public async Task<ProductionSettingsDto> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken);
        var stations = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.Code, cancellationToken);
        var steps = await db.ProcessSteps.AsNoTracking().OrderBy(s => s.Sequence).ToListAsync(cancellationToken);

        return new ProductionSettingsDto
        {
            LotQuantity = settings.LotQuantity,
            MaxLotQuantity = MaxLotQuantity(steps, stations),
            PartCode = settings.PartCode,
            SourceStationCode = settings.SourceStationCode,
            BufferStationCode = settings.BufferStationCode,
            Steps = steps.Select(s => new ProcessStepDto
            {
                Sequence = s.Sequence,
                StationCode = s.StationCode,
                StationName = stations[s.StationCode].Name,
                DurationSeconds = s.DurationSeconds,
            }).ToList(),
        };
    }

    /// <summary>修改批量與各站加工時間。已開始的加工不受影響；排隊中的批次開始時會採用新時間。</summary>
    public async Task<ProductionSettingsDto> UpdateSettingsAsync(ProductionSettingsDto request, CancellationToken cancellationToken)
    {
        var settings = await db.ProductionSettings.SingleAsync(cancellationToken);
        var stations = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.Code, cancellationToken);
        var steps = await db.ProcessSteps.OrderBy(s => s.Sequence).ToListAsync(cancellationToken);

        ValidateQuantity(request.LotQuantity, MaxLotQuantity(steps, stations), "lotQuantity");
        settings.LotQuantity = request.LotQuantity;

        foreach (var requested in request.Steps ?? [])
        {
            var step = steps.SingleOrDefault(s => s.Sequence == requested.Sequence)
                ?? throw new RequestValidationException("steps", $"Step {requested.Sequence} does not exist.");
            if (requested.DurationSeconds is < 1 or > MaxDurationSeconds)
            {
                throw new RequestValidationException("steps", $"Step {requested.Sequence}: durationSeconds must be between 1 and {MaxDurationSeconds}.");
            }

            step.DurationSeconds = requested.DurationSeconds;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Production settings updated: lot quantity {Quantity}, durations {Durations}.",
            settings.LotQuantity, string.Join(", ", steps.Select(s => $"{s.StationCode}={s.DurationSeconds}s")));
        return await GetSettingsAsync(cancellationToken);
    }

    /// <summary>
    /// 模擬收料：在原料倉建立一個批次，並建立送往第一道工序的搬運任務。
    /// 批次、位置異動、搬運任務在同一次 SaveChanges（同一筆交易）寫入。
    /// </summary>
    public async Task<MaterialLot> CreateLotAsync(int? quantity, CancellationToken cancellationToken)
    {
        var settings = await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken);
        var stations = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.Code, cancellationToken);
        var steps = await db.ProcessSteps.AsNoTracking().OrderBy(s => s.Sequence).ToListAsync(cancellationToken);

        var lotQuantity = quantity ?? settings.LotQuantity;
        ValidateQuantity(lotQuantity, MaxLotQuantity(steps, stations), "quantity");

        var now = time.GetUtcNow();
        var first = steps.FirstOrDefault();
        var lot = MaterialLot.Receive(settings.PartCode, lotQuantity, settings.SourceStationCode, first?.Sequence, now);

        db.Lots.Add(lot);
        db.InventoryMovements.Add(InventoryMovement.Record(lot, from: null, "Received", now));
        db.TransportTasks.Add(TransportTask.Create(
            lot, settings.SourceStationCode, first?.StationCode ?? settings.BufferStationCode, TransportTaskKind.Replenishment, now));

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Lot {LotNumber} received at {Station}: {Quantity} x {Part}.", lot.LotNumber, lot.StationCode, lot.Quantity, lot.PartCode);
        return lot;
    }

    /// <summary>批次剛卸到某站：還有工序就建立一筆排隊中的加工紀錄。由搬運完成事件在同一筆交易內呼叫。</summary>
    public void OnLotArrived(MaterialLot lot, DateTimeOffset now)
    {
        if (lot.Status == LotStatus.Queued)
        {
            db.Operations.Add(new OperationExecution
            {
                LotId = lot.Id,
                Lot = lot,
                StepSequence = lot.CurrentStepSequence!.Value,
                StationCode = lot.StationCode!,
                QueuedAt = now,
            });
        }
    }

    /// <summary>
    /// 推進加工：先完成到期的加工並建立下一趟搬運，再讓空閒的機台開始下一個排隊批次。
    /// 回傳這一輪改變的加工紀錄數。
    /// </summary>
    public async Task<int> AdvanceOperationsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var changed = 0;

        var due = await db.Operations.Include(o => o.Lot)
            .Where(o => o.Status == OperationStatus.Running && o.DueAt <= now)
            .ToListAsync(cancellationToken);
        if (due.Count > 0)
        {
            var steps = await db.ProcessSteps.AsNoTracking().OrderBy(s => s.Sequence).ToListAsync(cancellationToken);
            var buffer = (await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken)).BufferStationCode;

            foreach (var operation in due)
            {
                var next = steps.FirstOrDefault(s => s.Sequence > operation.StepSequence);
                operation.Complete(now);
                operation.Lot.FinishProcessing(next?.Sequence, now);
                db.TransportTasks.Add(TransportTask.Create(
                    operation.Lot, operation.StationCode, next?.StationCode ?? buffer, TransportTaskKind.WipTransfer, now));
                logger.LogInformation("Lot {LotNumber} finished step {Step} at {Station}; transport to {Next} requested.",
                    operation.Lot.LotNumber, operation.StepSequence, operation.StationCode, next?.StationCode ?? buffer);
            }

            await db.SaveChangesAsync(cancellationToken);
            changed += due.Count;
        }

        var queued = await db.Operations.Include(o => o.Lot)
            .Where(o => o.Status == OperationStatus.Queued)
            .OrderBy(o => o.QueuedAt)
            .ToListAsync(cancellationToken);
        if (queued.Count == 0)
        {
            return changed;
        }

        var busyStations = await db.Operations
            .Where(o => o.Status == OperationStatus.Running)
            .Select(o => o.StationCode)
            .ToListAsync(cancellationToken);

        // 完工件還沒被搬走的機台不開始下一批：完工件容量（30／45）的規則還沒定，先保守地只放一批。
        var stationCodes = queued.Select(o => o.StationCode).Distinct().ToList();
        var blockedStations = await db.Lots
            .Where(l => l.Status == LotStatus.AwaitingTransport && stationCodes.Contains(l.StationCode!))
            .Select(l => l.StationCode!)
            .ToListAsync(cancellationToken);
        var durations = await db.ProcessSteps.AsNoTracking().ToDictionaryAsync(s => s.Sequence, s => s.DurationSeconds, cancellationToken);

        var unavailable = busyStations.Concat(blockedStations).ToHashSet(StringComparer.Ordinal);
        foreach (var operation in queued)
        {
            // 每站一次只加工一批；同一輪內開始了一批，該站就算忙碌。
            if (!unavailable.Add(operation.StationCode))
            {
                continue;
            }

            operation.Start(durations[operation.StepSequence], now);
            operation.Lot.StartProcessing(now);
            changed++;
            logger.LogInformation("Lot {LotNumber} started step {Step} at {Station} for {Duration}s.",
                operation.Lot.LotNumber, operation.StepSequence, operation.StationCode, operation.DurationSeconds);
        }

        await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>
    /// P2 不拆批：整批一趟送進機邊，所以批量不能超過流程中最小的待加工料容量。
    /// </summary>
    private static int MaxLotQuantity(IEnumerable<ProcessStep> steps, IReadOnlyDictionary<string, Station> stations) =>
        steps.Select(s => stations[s.StationCode].InboundCapacity).Where(c => c is not null).Min() ?? int.MaxValue;

    private static void ValidateQuantity(int quantity, int max, string field)
    {
        if (quantity < 1 || quantity > max)
        {
            throw new RequestValidationException(field, $"Quantity must be between 1 and {max}.");
        }
    }
}
