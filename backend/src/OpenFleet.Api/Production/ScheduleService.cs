using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Catalog;
using OpenFleet.Api.Data;
using OpenFleet.Api.Traffic;
using OpenFleet.Contracts;
using static OpenFleet.Api.Production.ScheduleForecaster;

namespace OpenFleet.Api.Production;

/// <summary>
/// 排程預估（P6 第 3 步，唯讀）：讀出現在每一框、每台機台、每座爐的狀態，交給 <see cref="ScheduleForecaster"/> 往後推算。
/// 一律用正常速度算一次，交期只跟它比（使用者 2026-10-23）；測試模式開著時再用目前的倍率算一次，示範時看大約多久做完。
/// </summary>
public sealed class ScheduleService(OpenFleetDbContext db, RoadNetwork network, TimeProvider time)
{
    /// <summary>
    /// 預估用的載貨車速（公尺／秒）。後端不知道車的實際速度（在模擬器的設定裡：空車 2、滿載 1），取滿載的 1 m/s。
    /// </summary>
    public const double VehicleMetersPerSecond = 1;

    public async Task<ScheduleDto> GetAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var snapshot = await LoadAsync(now, cancellationToken);
        var settings = snapshot.Input.Settings;

        var normal = Run(snapshot.Input, new Rates(_ => 1, settings.AgvHandoverSeconds, VehicleMetersPerSecond, TestModeRules: false, TruckSpeed: 1));
        var accelerated = settings.TestMode
            ? Run(snapshot.Input, new Rates(
                settings.SpeedOf, settings.EffectiveHandoverSeconds, VehicleMetersPerSecond * settings.TestLoadedVehicleSpeed,
                TestModeRules: true, settings.TruckSpeed))
            : null;

        return new ScheduleDto
        {
            GeneratedAt = now,
            TestMode = settings.TestMode,
            Normal = Map(snapshot, normal, now, compareDueDates: true),
            Accelerated = accelerated is null ? null : Map(snapshot, accelerated, now, compareDueDates: false),
        };
    }

    private sealed record Snapshot(
        Input Input,
        IReadOnlyList<Machine> Machines,
        IReadOnlyDictionary<string, Station> Stations,
        IReadOnlyList<SalesOrder> Orders,
        IReadOnlySet<Guid> OrdersWithBoxesOnTheWay);

    private async Task<Snapshot> LoadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken);
        var steps = await db.ProcessSteps.AsNoTracking().OrderBy(s => s.Sequence).ToListAsync(cancellationToken);
        var stations = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.Code, cancellationToken);
        var machines = await db.Machines.AsNoTracking().OrderBy(m => m.StationCode).ThenBy(m => m.Number).ToListAsync(cancellationToken);
        var tools = await db.MachineTools.AsNoTracking().ToDictionaryAsync(t => t.MachineCode, cancellationToken);
        var truck = await db.TruckSchedules.AsNoTracking().SingleAsync(cancellationToken);

        // 還在製程中、或包好了在等裝箱的料框（出貨箱另外看）。
        var lots = await db.Lots.AsNoTracking()
            .Where(l => !l.IsBox && l.Status != LotStatus.Boxed && l.Status != LotStatus.Shipped && l.Status != LotStatus.Scrapped
                && !(l.Status == LotStatus.Staged && l.CurrentStepSequence == null))
            .ToListAsync(cancellationToken);
        var lotIds = lots.Select(l => l.Id).ToList();

        var workOrderIds = lots.Select(l => l.ProductionOrderId).OfType<Guid>().Distinct().ToList();
        var workOrders = await (
                from workOrder in db.ProductionOrders.AsNoTracking()
                join line in db.SalesOrderLines.AsNoTracking() on workOrder.SalesOrderLineId equals line.Id
                where workOrderIds.Contains(workOrder.Id)
                select new { workOrder.Id, line.SalesOrderId, workOrder.Spec.RoughSecondsPerPiece, workOrder.Spec.FinishSecondsPerPiece })
            .ToDictionaryAsync(w => w.Id, cancellationToken);

        // 還沒出貨的出貨單：箱子都已經在出貨站的，貨車一來就能載。
        var openDeliveries = await db.Deliveries.AsNoTracking().Where(d => d.ShippedAt == null)
            .Select(d => new { d.Id, d.SalesOrderId }).ToListAsync(cancellationToken);
        var openDeliveryIds = openDeliveries.Select(d => d.Id).ToList();
        var boxes = await db.Lots.AsNoTracking()
            .Where(l => l.DeliveryId != null && openDeliveryIds.Contains(l.DeliveryId.Value))
            .Select(l => new { DeliveryId = l.DeliveryId!.Value, l.Status, l.StationCode })
            .ToListAsync(cancellationToken);
        var boxesOnTheWay = boxes
            .Where(b => !(b.Status == LotStatus.Staged && b.StationCode == settings.BufferStationCode))
            .Select(b => openDeliveries.Single(d => d.Id == b.DeliveryId).SalesOrderId)
            .ToHashSet();

        var orderIds = workOrders.Values.Select(w => w.SalesOrderId).Concat(openDeliveries.Select(d => d.SalesOrderId)).Distinct().ToList();
        var orders = await db.SalesOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id))
            .OrderBy(o => o.CreatedAt).ToListAsync(cancellationToken);
        var orderNumbers = orders.ToDictionary(o => o.Id, o => o.Number);

        var priorities = await SchedulePriorities.ForLotsAsync(db, lots, cancellationToken);
        var operations = await db.Operations.AsNoTracking()
            .Where(o => (o.Status == OperationStatus.Queued || o.Status == OperationStatus.Running) && lotIds.Contains(o.LotId))
            .ToListAsync(cancellationToken);
        var operationByLot = operations.GroupBy(o => o.LotId).ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.QueuedAt).First());
        var cutQuantities = await db.CuttingJobs.AsNoTracking()
            .Where(j => lotIds.Contains(j.LotId) && j.CompletedAt == null)
            .ToDictionaryAsync(j => j.LotId, j => j.PiecesToCut, cancellationToken);
        var tasks = (await db.TransportTasks.AsNoTracking()
                .Where(t => t.LotId != null && lotIds.Contains(t.LotId.Value)
                    && (t.Status == TransportTaskStatus.Pending || t.Status == TransportTaskStatus.Assigned || t.Status == TransportTaskStatus.Executing))
                .Select(t => new { LotId = t.LotId!.Value, t.FromStationCode, t.ToStationCode, t.CreatedAt })
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.LotId)
            .ToDictionary(g => g.Key, g => g.MaxBy(t => t.CreatedAt)!);

        var runningMachines = operations.Where(o => o.Status == OperationStatus.Running && o.MachineCode != null)
            .Select(o => o.MachineCode!).ToHashSet(StringComparer.Ordinal);
        var pieceStations = steps.Where(s => s.Kind == ProcessStepKind.Piece).Select(s => s.StationCode).ToList();
        var holding = (await MachineQueries.BlockedMachinesAsync(db, pieceStations, machines, cancellationToken)).ToHashSet(StringComparer.Ordinal);

        double SpeedAt(string stationCode) => steps.FirstOrDefault(s => s.StationCode == stationCode) is { } step ? settings.SpeedOf(step) : 1;

        // 換刀還剩多久，或停機等刀（當作新刀馬上到）要換多久；原本的秒數。
        double ToolStop(MachineTool? tool, double speed) =>
            tool is null ? 0
            : tool.ChangeDueAt is { } due ? Math.Max(0, (due - now).TotalSeconds) * speed
            : tool.IsWaitingForSpare ? settings.ToolChangeSeconds
            : 0;

        MachineTool? ToolOf(string machineCode, string stationCode) =>
            steps.Any(s => s.StationCode == stationCode && s.UsesTool) ? tools.GetValueOrDefault(machineCode) : null;

        var machineInputs = new List<MachineInput>();
        foreach (var machine in machines)
        {
            var last = await db.Operations.AsNoTracking()
                .Where(o => o.MachineCode == machine.Code && o.StartedAt != null)
                .OrderByDescending(o => o.StartedAt)
                .Select(o => new MachineAssignment.Setup(o.Lot.BatchNumber, o.Lot.PartCode, o.Lot.RodDiameterMm))
                .FirstOrDefaultAsync(cancellationToken);
            var tool = ToolOf(machine.Code, machine.StationCode);
            var running = runningMachines.Contains(machine.Code);
            machineInputs.Add(new MachineInput
            {
                Code = machine.Code,
                StationCode = machine.StationCode,
                Number = machine.Number,
                Last = last,
                ToolPieces = tool is null || tool.IsStopped ? 0 : tool.PiecesSinceChange,
                StopSeconds = running ? 0 : ToolStop(tool, SpeedAt(machine.StationCode)),
                HoldingFinishedBin = !running && holding.Contains(machine.Code),
            });
        }

        var lotInputs = new List<LotInput>();
        foreach (var lot in lots.OrderBy(l => operationByLot.GetValueOrDefault(l.Id)?.QueuedAt ?? l.UpdatedAt).ThenBy(l => l.LotNumber))
        {
            var workOrder = lot.ProductionOrderId is { } workOrderId ? workOrders.GetValueOrDefault(workOrderId) : null;
            var operation = operationByLot.GetValueOrDefault(lot.Id);
            var task = tasks.GetValueOrDefault(lot.Id);
            var overrides = new Dictionary<string, double>();
            if (workOrder?.RoughSecondsPerPiece is { } rough)
            {
                overrides[CatalogService.RoughStationCode] = rough;
            }

            if (workOrder?.FinishSecondsPerPiece is { } finish)
            {
                overrides[CatalogService.FinishStationCode] = finish;
            }

            var phase = lot.Status switch
            {
                LotStatus.AwaitingMaterial => LotPhase.WaitingForMaterial,
                LotStatus.AwaitingTransport or LotStatus.InTransit => LotPhase.Moving,
                LotStatus.Queued => LotPhase.Queued,
                LotStatus.Processing when operation?.BatchRunId is not null => LotPhase.InFurnace,
                LotStatus.Processing when operation is { MachineCode: not null } => LotPhase.Running,
                // 還沒建立加工紀錄的（同一輪裡剛到站）當作排隊。
                LotStatus.Processing => LotPhase.Queued,
                _ => LotPhase.Finished,
            };

            var lotInput = new LotInput
            {
                Id = lot.Id,
                LotNumber = lot.LotNumber,
                BatchNumber = lot.BatchNumber,
                PartCode = lot.PartCode,
                RodDiameterMm = lot.RodDiameterMm,
                Quantity = lot.Quantity,
                IsMakeup = lot.IsMakeup,
                ReworkCount = lot.ReworkCount,
                SalesOrderId = workOrder?.SalesOrderId,
                SalesOrderNumber = workOrder is null ? null : orderNumbers.GetValueOrDefault(workOrder.SalesOrderId),
                Priority = priorities.GetValueOrDefault(lot.Id, SchedulePriority.None),
                Step = lot.CurrentStepSequence,
                Phase = phase,
                FromStation = task?.FromStationCode ?? lot.StationCode,
                ToStation = task?.ToStationCode,
                OnVehicle = lot.Status == LotStatus.InTransit,
                QueuedAt = operation is null ? 0 : (operation.QueuedAt - now).TotalSeconds,
                SecondsPerPiece = overrides,
                CutQuantity = cutQuantities.TryGetValue(lot.Id, out var cut) ? cut : null,
            };

            lotInputs.Add(phase == LotPhase.Running ? WithRunning(lotInput, operation!) : lotInput);
        }

        LotInput WithRunning(LotInput input, OperationExecution operation)
        {
            var step = steps.Single(s => s.Sequence == operation.StepSequence);
            var speed = settings.SpeedOf(step);
            var cycle = (operation.SecondsPerPiece ?? step.SecondsPerPiece) + 2 * operation.HandlingSeconds;
            var tool = ToolOf(operation.MachineCode!, step.StationCode);
            double lead;
            if (tool is { IsStopped: true })
            {
                // 換完刀，目前這一件從頭開始。
                lead = ToolStop(tool, speed);
            }
            else if (operation.ChangeoverDueAt is { } changeoverDue && changeoverDue > now)
            {
                lead = (changeoverDue - now).TotalSeconds * speed;
            }
            else
            {
                var pieceStarted = operation.PieceStartedAt ?? now;
                var from = settings.SpeedChangedAt > pieceStarted ? settings.SpeedChangedAt.Value : pieceStarted;
                lead = -Math.Min(cycle, Math.Max(0, (now - from).TotalSeconds * speed));
            }

            return new LotInput
            {
                Id = input.Id,
                LotNumber = input.LotNumber,
                BatchNumber = input.BatchNumber,
                PartCode = input.PartCode,
                RodDiameterMm = input.RodDiameterMm,
                Quantity = operation.Quantity,
                IsMakeup = input.IsMakeup,
                ReworkCount = input.ReworkCount,
                SalesOrderId = input.SalesOrderId,
                SalesOrderNumber = input.SalesOrderNumber,
                Priority = input.Priority,
                Step = input.Step,
                Phase = LotPhase.Running,
                SecondsPerPiece = input.SecondsPerPiece,
                MachineCode = operation.MachineCode,
                StartedAt = ((operation.StartedAt ?? now) - now).TotalSeconds,
                ChangedOver = operation.ChangeoverDueAt is not null,
                RemainingPieces = operation.Quantity - operation.PiecesCompleted,
                CycleSeconds = cycle,
                LeadSeconds = lead,
            };
        }

        var runs = await db.BatchRuns.AsNoTracking().Where(r => r.CompletedAt == null).ToListAsync(cancellationToken);
        var runInputs = runs.Select(run => new RunInput
        {
            StationCode = run.StationCode,
            StartedAt = (run.StartedAt - now).TotalSeconds,
            RemainingSeconds = Math.Max(0, run.RemainingSeconds - Math.Max(0, (now - run.AccruedAt).TotalSeconds) * SpeedAt(run.StationCode)),
            LotIds = operations.Where(o => o.BatchRunId == run.Id).Select(o => o.LotId).ToList(),
        }).ToList();

        var lastRuns = (await db.BatchRuns.AsNoTracking()
                .Where(r => r.CompletedAt != null)
                .GroupBy(r => r.StationCode)
                .Select(g => new { Station = g.Key, CompletedAt = g.Max(r => r.CompletedAt) })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Station, x => (x.CompletedAt!.Value - now).TotalSeconds);

        var truckRemaining = truck.RemainingSeconds
            - (truck.AccruedAt is { } accrued ? Math.Max(0, (now - accrued).TotalSeconds) * settings.TruckSpeed : 0);

        var input = new Input
        {
            Steps = steps,
            Settings = settings,
            Machines = machineInputs,
            Lots = lotInputs,
            Runs = runInputs,
            TruckRemainingSeconds = truckRemaining,
            Distance = (from, to) => Distance(stations, from, to),
            LastRunCompletedAt = lastRuns,
        };
        return new Snapshot(input, machines, stations, orders, boxesOnTheWay);
    }

    /// <summary>沿路網的距離；路網還沒載入或找不到路時，用兩個交接點的直角距離。</summary>
    private double Distance(IReadOnlyDictionary<string, Station> stations, string from, string to)
    {
        if (!stations.TryGetValue(from, out var a) || !stations.TryGetValue(to, out var b))
        {
            return 0;
        }

        if (network.Nodes.Count > 0)
        {
            var meters = network.PathLength(network.Nearest(a.HandoffX, a.HandoffZ).Code, network.Nearest(b.HandoffX, b.HandoffZ).Code);
            if (!double.IsInfinity(meters))
            {
                return meters;
            }
        }

        return Math.Abs(a.HandoffX - b.HandoffX) + Math.Abs(a.HandoffZ - b.HandoffZ);
    }

    private static ScheduleForecastDto Map(Snapshot snapshot, Result result, DateTimeOffset now, bool compareDueDates)
    {
        var input = snapshot.Input;
        var lotsByOrder = input.Lots.Where(l => l.SalesOrderId is not null).ToLookup(l => l.SalesOrderId!.Value);
        var packing = input.Steps.FirstOrDefault(s => s.IsPacking)?.StationCode ?? input.Settings.BufferStationCode;

        return new ScheduleForecastDto
        {
            Machines = snapshot.Machines.Select(machine => new MachineScheduleDto
            {
                Code = machine.Code,
                Name = machine.Name,
                StationCode = machine.StationCode,
                Bins = result.Bins.Where(b => b.MachineCode == machine.Code).OrderBy(b => b.Start).Select(bin => new ScheduledBinDto
                {
                    LotNumber = bin.Lot.LotNumber,
                    BatchNumber = bin.Lot.BatchNumber,
                    SalesOrderNumber = bin.Lot.SalesOrderNumber,
                    IsRush = bin.Lot.Priority.Rush,
                    PartCode = bin.Lot.PartCode,
                    RodDiameterMm = bin.Lot.RodDiameterMm,
                    Quantity = bin.Quantity,
                    Running = bin.Running,
                    Changeover = bin.Changeover,
                    StartAt = now.AddSeconds(bin.Start),
                    EndAt = now.AddSeconds(bin.End),
                }).ToList(),
            }).ToList(),
            Furnaces = input.Steps.Where(s => s.Kind == ProcessStepKind.Batch).Select(step => new FurnaceScheduleDto
            {
                StationCode = step.StationCode,
                Name = snapshot.Stations.TryGetValue(step.StationCode, out var station) ? station.Name : step.StationCode,
                Runs = result.Runs.Where(r => r.StationCode == step.StationCode).OrderBy(r => r.Start).Select(run => new ScheduledBatchRunDto
                {
                    Running = run.Running,
                    Pieces = run.Pieces,
                    LotNumbers = run.Lots.Select(l => l.LotNumber).ToList(),
                    StartAt = now.AddSeconds(run.Start),
                    EndAt = now.AddSeconds(run.End),
                }).ToList(),
                StrandedLotNumbers = result.Stranded.GetValueOrDefault(step.StationCode)?.Select(l => l.LotNumber).ToList() ?? [],
                StrandedPieces = result.Stranded.GetValueOrDefault(step.StationCode)?.Sum(l => l.Quantity) ?? 0,
            }).ToList(),
            Orders = snapshot.Orders.Select(order =>
            {
                var lots = lotsByOrder[order.Id].ToList();
                var dto = new OrderForecastDto
                {
                    Id = order.Id,
                    Number = order.Number,
                    Customer = order.Customer,
                    DueDate = order.DueDate?.ToDateTime(TimeOnly.MinValue),
                    IsRush = order.IsRush,
                };

                var blocked = lots.Where(l => result.Blocked.ContainsKey(l.Id)).ToList();
                if (blocked.Count > 0)
                {
                    dto.Blocker = blocked.Any(l => result.Blocked[l.Id] == ForecastBlocker.WaitingForMaterial)
                        ? ForecastBlocker.WaitingForMaterial
                        : ForecastBlocker.WaitingForFurnaceFill;
                    dto.BlockedLotNumbers = blocked.Select(l => l.LotNumber).ToList();
                    return dto;
                }

                dto.Finished = lots.All(l => l.Phase == LotPhase.Finished);
                var finish = lots.Count == 0 ? 0 : lots.Max(l => result.FinishedAt.GetValueOrDefault(l.Id));
                dto.FinishAt = dto.Finished ? null : now.AddSeconds(finish);
                // 做完之後裝箱、送到出貨站，等下一班貨車。已經在出貨站的箱子直接等貨車。
                var ready = dto.Finished && !snapshot.OrdersWithBoxesOnTheWay.Contains(order.Id) && lots.Count == 0
                    ? 0
                    : finish + result.HopSeconds(packing, input.Settings.BufferStationCode);
                var ship = now.AddSeconds(result.NextTruckAfter(ready));
                dto.ShipAt = ship;
                if (compareDueDates && order.DueDate is { } due)
                {
                    dto.OnTime = ship < new DateTimeOffset(due.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                }

                return dto;
            }).ToList(),
        };
    }
}
