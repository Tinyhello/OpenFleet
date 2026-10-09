using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Simulation;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Production;

/// <summary>輸入不符合規則；端點會轉成 HTTP 400（ValidationProblem）。</summary>
public sealed class RequestValidationException(string field, string message) : Exception(message)
{
    public IDictionary<string, string[]> Errors { get; } = new Dictionary<string, string[]> { [field] = [message] };
}

/// <summary>
/// 批次、製程設定與加工進度（Scoped：跟著一個 DbContext）。
/// 加工是後端內的簡化模擬：每站一台、先到先做、逐件計數、整批做完才放行；刀具到達壽命時機台暫停換刀。
/// </summary>
public sealed class ProductionService(OpenFleetDbContext db, SimulationSpeed speed, TimeProvider time, ILogger<ProductionService> logger)
{
    public const double MaxSecondsPerPiece = 600;
    public const double MaxHandoverSecondsPerPiece = 60;
    public const double MaxToolChangeSeconds = 3600;
    public const int MaxToolLifePieces = 100_000;

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
            HandoverSecondsPerPiece = settings.HandoverSecondsPerPiece,
            ToolChangeSeconds = settings.ToolChangeSeconds,
            Steps = steps.Select(s => new ProcessStepDto
            {
                Sequence = s.Sequence,
                StationCode = s.StationCode,
                StationName = stations[s.StationCode].Name,
                SecondsPerPiece = s.SecondsPerPiece,
                ToolLifePieces = s.ToolLifePieces,
            }).ToList(),
        };
    }

    /// <summary>
    /// 修改示範設定。已開始的加工與已派出的搬運不受影響；之後開始的加工、之後派出的搬運採用新值。
    /// </summary>
    public async Task<ProductionSettingsDto> UpdateSettingsAsync(ProductionSettingsDto request, CancellationToken cancellationToken)
    {
        var settings = await db.ProductionSettings.SingleAsync(cancellationToken);
        var stations = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.Code, cancellationToken);
        var steps = await db.ProcessSteps.OrderBy(s => s.Sequence).ToListAsync(cancellationToken);

        ValidateQuantity(request.LotQuantity, MaxLotQuantity(steps, stations), "lotQuantity");
        // 0 代表瞬間完成（測試用）；負數或太大的值視為輸入錯誤。
        ValidateRange(request.HandoverSecondsPerPiece, 0, MaxHandoverSecondsPerPiece, "handoverSecondsPerPiece");
        ValidateRange(request.ToolChangeSeconds, 0, MaxToolChangeSeconds, "toolChangeSeconds");

        settings.LotQuantity = request.LotQuantity;
        settings.HandoverSecondsPerPiece = request.HandoverSecondsPerPiece;
        settings.ToolChangeSeconds = request.ToolChangeSeconds;

        foreach (var requested in request.Steps ?? [])
        {
            var step = steps.SingleOrDefault(s => s.Sequence == requested.Sequence)
                ?? throw new RequestValidationException("steps", $"Step {requested.Sequence} does not exist.");
            if (!(requested.SecondsPerPiece > 0 && requested.SecondsPerPiece <= MaxSecondsPerPiece))
            {
                throw new RequestValidationException("steps", $"Step {requested.Sequence}: secondsPerPiece must be greater than 0 and at most {MaxSecondsPerPiece}.");
            }

            if (requested.ToolLifePieces is < 1 or > MaxToolLifePieces)
            {
                throw new RequestValidationException("steps", $"Step {requested.Sequence}: toolLifePieces must be between 1 and {MaxToolLifePieces}.");
            }

            step.SecondsPerPiece = requested.SecondsPerPiece;
            step.ToolLifePieces = requested.ToolLifePieces;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Production settings updated: lot {Quantity}, handover {Handover}s/piece, tool change {ToolChange}s, steps {Steps}.",
            settings.LotQuantity, settings.HandoverSecondsPerPiece, settings.ToolChangeSeconds,
            string.Join(", ", steps.Select(s => $"{s.StationCode}={s.SecondsPerPiece}s/piece, tool {s.ToolLifePieces}")));
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
                Quantity = lot.Quantity,
                QueuedAt = now,
            });
        }
    }

    /// <summary>
    /// 把加工推進到現在：逐件完成、到達刀具壽命時換刀、整批做完時建立下一趟搬運，最後讓空閒的機台開始下一批。
    /// 每一件與每次換刀的時間點都由資料庫裡的時間推算，所以不管多久呼叫一次（包含後端停機之後）結果都一樣。
    /// 回傳這一輪是否有任何變更。
    /// </summary>
    public async Task<bool> AdvanceOperationsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var (timeScale, scaleChangedAt) = speed.Current;
        var steps = await db.ProcessSteps.AsNoTracking().OrderBy(s => s.Sequence).ToListAsync(cancellationToken);
        var settings = await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken);
        var tools = await db.StationTools.ToDictionaryAsync(t => t.StationCode, cancellationToken);
        var running = await db.Operations.Include(o => o.Lot)
            .Where(o => o.Status == OperationStatus.Running)
            .ToListAsync(cancellationToken);

        foreach (var operation in running)
        {
            var tool = tools[operation.StationCode];
            var step = steps.Single(s => s.Sequence == operation.StepSequence);

            while (operation.Status == OperationStatus.Running)
            {
                if (tool.ChangeDueAt is { } changeDue)
                {
                    if (changeDue > now)
                    {
                        break;
                    }

                    // 換刀結束：目前這一件從換刀完成的時間重新開始。
                    FinishToolChange(tool);
                    operation.ResumeAt(changeDue);
                }

                var pieceDue = operation.PieceDueAt(timeScale, scaleChangedAt);
                if (pieceDue > now)
                {
                    break;
                }

                operation.CompletePiece(pieceDue);
                tool.CountPiece();
                if (tool.PiecesSinceChange >= step.ToolLifePieces)
                {
                    tool.StartChange(pieceDue, settings.ToolChangeSeconds / timeScale);
                    logger.LogInformation("Station {Station}: tool reached {Pieces} piece(s); changing for {Seconds}s.",
                        tool.StationCode, tool.PiecesSinceChange, settings.ToolChangeSeconds);
                }
            }

            if (operation.Status == OperationStatus.Completed)
            {
                // 整批完成才放行：改為等待搬運，並建立送往下一站（或暫存點）的任務。
                var next = steps.FirstOrDefault(s => s.Sequence > operation.StepSequence);
                var destination = next?.StationCode ?? settings.BufferStationCode;
                operation.Lot.FinishProcessing(next?.Sequence, now);
                db.TransportTasks.Add(TransportTask.Create(operation.Lot, operation.StationCode, destination, TransportTaskKind.WipTransfer, now));
                logger.LogInformation("Lot {LotNumber} finished step {Step} at {Station}; transport to {Next} requested.",
                    operation.Lot.LotNumber, operation.StepSequence, operation.StationCode, destination);
            }
        }

        // 沒有在加工的機台，換刀到期也要結束（上一批最後一件剛好用完刀具的情況）。
        foreach (var tool in tools.Values.Where(t => t.ChangeDueAt <= now))
        {
            FinishToolChange(tool);
        }

        var changed = db.ChangeTracker.HasChanges();
        await db.SaveChangesAsync(cancellationToken);
        return await StartQueuedOperationsAsync(steps, tools, now, cancellationToken) || changed;
    }

    private async Task<bool> StartQueuedOperationsAsync(
        IReadOnlyList<ProcessStep> steps, IReadOnlyDictionary<string, StationTool> tools, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queued = await db.Operations.Include(o => o.Lot)
            .Where(o => o.Status == OperationStatus.Queued)
            .OrderBy(o => o.QueuedAt)
            .ToListAsync(cancellationToken);
        if (queued.Count == 0)
        {
            return false;
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

        var unavailable = busyStations
            .Concat(blockedStations)
            .Concat(tools.Values.Where(t => t.IsChanging).Select(t => t.StationCode))
            .ToHashSet(StringComparer.Ordinal);
        var started = false;

        foreach (var operation in queued)
        {
            // 每站一次只加工一批；同一輪內開始了一批，該站就算忙碌。
            if (!unavailable.Add(operation.StationCode))
            {
                continue;
            }

            var step = steps.Single(s => s.Sequence == operation.StepSequence);
            operation.Start(step.SecondsPerPiece, now);
            operation.Lot.StartProcessing(now);
            started = true;
            logger.LogInformation("Lot {LotNumber} started step {Step} at {Station}: {Quantity} piece(s), {Seconds}s each.",
                operation.Lot.LotNumber, operation.StepSequence, operation.StationCode, operation.Quantity, step.SecondsPerPiece);
        }

        await db.SaveChangesAsync(cancellationToken);
        return started;
    }

    /// <summary>
    /// 每個站目前的物料與設備狀態，供顯示端畫備料區、成品區、加工與刀具的進度條。唯讀查詢。
    /// </summary>
    public async Task<List<StationStatusDto>> GetStationStatusAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var stations = await db.Stations.AsNoTracking().OrderBy(s => s.Code).ToListAsync(cancellationToken);
        var steps = await db.ProcessSteps.AsNoTracking().ToDictionaryAsync(s => s.StationCode, cancellationToken);
        var tools = await db.StationTools.AsNoTracking().ToDictionaryAsync(t => t.StationCode, cancellationToken);
        var running = await db.Operations.AsNoTracking()
            .Where(o => o.Status == OperationStatus.Running)
            .Select(o => new { o.StationCode, o.Lot.LotNumber, o.PiecesCompleted, o.Quantity })
            .ToDictionaryAsync(o => o.StationCode, cancellationToken);
        var lots = await db.Lots.AsNoTracking()
            .Where(l => l.StationCode != null && l.Status != LotStatus.Processing)
            .GroupBy(l => new { l.StationCode, l.Status })
            .Select(g => new { g.Key.StationCode, g.Key.Status, Quantity = g.Sum(l => l.Quantity) })
            .ToListAsync(cancellationToken);

        int Sum(string station, LotStatus status) =>
            lots.Where(l => l.StationCode == station && l.Status == status).Sum(l => l.Quantity);

        return stations.Select(station =>
        {
            var machining = running.GetValueOrDefault(station.Code);
            var status = new StationStatusDto
            {
                Code = station.Code,
                Name = station.Name,
                IsMachine = steps.ContainsKey(station.Code),
                // 加工中的批次：還沒做的件數在備料區，做完的件數在成品區。
                InboundQuantity = Sum(station.Code, LotStatus.Queued) + Sum(station.Code, LotStatus.Staged)
                    + (machining is null ? 0 : machining.Quantity - machining.PiecesCompleted),
                InboundCapacity = station.InboundCapacity,
                OutboundQuantity = Sum(station.Code, LotStatus.AwaitingTransport) + (machining?.PiecesCompleted ?? 0),
                OutboundCapacity = station.OutboundCapacity,
                Machining = machining is null ? null : new MachiningStatusDto
                {
                    LotNumber = machining.LotNumber,
                    PiecesCompleted = machining.PiecesCompleted,
                    Quantity = machining.Quantity,
                },
            };

            if (steps.TryGetValue(station.Code, out var step) && tools.TryGetValue(station.Code, out var tool))
            {
                status.Tool = new ToolStatusDto
                {
                    PiecesUsed = tool.PiecesSinceChange,
                    LifePieces = step.ToolLifePieces,
                    Changing = tool.IsChanging,
                    ChangeProgress = ChangeProgress(tool, now),
                };
            }

            return status;
        }).ToList();
    }

    private void FinishToolChange(StationTool tool)
    {
        var record = tool.CompleteChange();
        db.ToolChanges.Add(record);
        logger.LogInformation("Station {Station}: tool changed after {Pieces} piece(s).", record.StationCode, record.PiecesUsed);
    }

    private static double ChangeProgress(StationTool tool, DateTimeOffset now)
    {
        if (tool.ChangeStartedAt is not { } started || tool.ChangeDueAt is not { } due)
        {
            return 0;
        }

        var total = (due - started).TotalSeconds;
        return total <= 0 ? 1 : Math.Clamp((now - started).TotalSeconds / total, 0, 1);
    }

    /// <summary>
    /// 目前不拆批：整批一趟送進機邊，所以批量不能超過流程中最小的待加工料容量。
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

    private static void ValidateRange(double value, double min, double max, string field)
    {
        // 寫成「不在範圍內」的否定，NaN 也會被擋下。
        if (!(value >= min && value <= max))
        {
            throw new RequestValidationException(field, $"Value must be between {min} and {max}.");
        }
    }
}
