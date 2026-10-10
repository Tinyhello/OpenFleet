using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Simulation;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Transport;

/// <summary>這一輪叫不到車的任務，以及叫不到的原因。</summary>
public sealed record WaitingTask(TransportTask Task, ShortageReason Reason);

/// <summary>
/// 缺車紀錄（Scoped）。派工迴圈每一輪告訴它「哪些任務這一輪叫不到車、為什麼」，它負責兩件事：
/// 1. 任務的等車區間：從叫不到車起，到派出車為止。
/// 2. 機台的耽誤區間：等待期間，相關機台真的停下來的時間（使用者 2026-10-09 確認的算法）。
/// 原因分兩種（使用者 2026-10-10）：車都在忙是「缺車」，代表車不夠用、不算緊急；
/// 該車型全部離線是「重大事故」，另外標記，不算進缺車的統計。
/// 缺料（沒有批次）、目的站放不下、派出之後的行駛與交通等待都不會進到這裡，所以不會混在一起算。
/// </summary>
public sealed class VehicleShortageService(OpenFleetDbContext db, SimulationSpeed speed, TimeProvider time, ILogger<VehicleShortageService> logger)
{
    /// <summary><paramref name="waiting"/> 依派工優先順序排列。</summary>
    public async Task RecordAsync(IReadOnlyList<WaitingTask> waiting, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var timeScale = speed.Current.TimeScale;

        var openWaits = await db.VehicleWaits.Where(w => w.EndedAt == null).ToListAsync(cancellationToken);
        var openDelays = await db.MachineDelays.Where(d => d.EndedAt == null).ToListAsync(cancellationToken);
        if (waiting.Count == 0 && openWaits.Count == 0 && openDelays.Count == 0)
        {
            return;
        }

        var newWaits = waiting.ToDictionary(w => w.Task.Id);
        foreach (var wait in openWaits)
        {
            wait.Accrue(now, timeScale);
            if (newWaits.TryGetValue(wait.TaskId, out var current) && current.Reason == wait.Reason)
            {
                newWaits.Remove(wait.TaskId);
            }
            else
            {
                wait.Close(now);
            }
        }

        var stopped = await FindStoppedMachinesAsync(waiting, cancellationToken);
        foreach (var delay in openDelays)
        {
            delay.Accrue(now, timeScale);
            if (stopped.TryGetValue(delay.StationCode, out var current) && current.Cause == delay.Cause && current.Reason == delay.Reason)
            {
                stopped.Remove(delay.StationCode);
            }
            else
            {
                delay.Close(now);
                logger.LogInformation("Station {Station}: delay ended after {Seconds:0.#}s ({Cause}, {Reason}).",
                    delay.StationCode, delay.SimulatedSeconds, delay.Cause, delay.Reason);
            }
        }

        // 先存結束的區間再新增：原因改變時，「一個任務／一台機台只有一筆進行中」的唯一索引才不會擋下新的那一筆。
        await db.SaveChangesAsync(cancellationToken);

        foreach (var (task, reason) in waiting.Where(w => newWaits.ContainsKey(w.Task.Id)))
        {
            db.VehicleWaits.Add(VehicleWait.Open(task.Id, reason, now));
            if (reason == ShortageReason.FleetOffline)
            {
                logger.LogError("Task {TaskId} ({From} -> {To}) cannot be dispatched: every vehicle of the required type is offline.",
                    task.Id, task.FromStationCode, task.ToStationCode);
            }
            else
            {
                logger.LogInformation("Task {TaskId} ({From} -> {To}) is waiting: all vehicles are busy.", task.Id, task.FromStationCode, task.ToStationCode);
            }
        }

        foreach (var (station, machine) in stopped)
        {
            db.MachineDelays.Add(MachineDelay.Open(station, machine.Step, machine.Cause, machine.Reason, machine.TaskId, now));
            logger.LogInformation("Station {Station} is stopped: {Cause} ({Reason}).", station, machine.Cause, machine.Reason);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record StoppedMachine(int Step, MachineDelayCause Cause, ShortageReason Reason, Guid TaskId);

    /// <summary>
    /// 等車的任務讓哪些機台停了下來。機台正在加工或換刀就不算。
    /// 等刀：機台已經到刀具壽命、停機等新刀，而送刀的任務叫不到車。
    /// 成品沒被搬走：取料站是機台，做完的批次沒被搬走，而且後面還有批次排隊。
    /// 等不到料：卸料站是機台，機台沒有東西可做，也沒有別的料正在送來。
    /// </summary>
    private async Task<Dictionary<string, StoppedMachine>> FindStoppedMachinesAsync(
        IReadOnlyList<WaitingTask> waiting, CancellationToken cancellationToken)
    {
        var stopped = new Dictionary<string, StoppedMachine>(StringComparer.Ordinal);
        if (waiting.Count == 0)
        {
            return stopped;
        }

        var steps = await db.ProcessSteps.AsNoTracking().ToDictionaryAsync(s => s.StationCode, s => s.Sequence, cancellationToken);
        var stations = waiting.SelectMany(w => new[] { w.Task.FromStationCode, w.Task.ToStationCode }).Where(steps.ContainsKey).Distinct().ToList();
        if (stations.Count == 0)
        {
            return stopped;
        }

        var operations = await db.Operations.AsNoTracking()
            .Where(o => stations.Contains(o.StationCode) && o.Status != OperationStatus.Completed)
            .Select(o => new { o.StationCode, o.Status })
            .ToListAsync(cancellationToken);
        var running = operations.Where(o => o.Status == OperationStatus.Running).Select(o => o.StationCode).ToHashSet(StringComparer.Ordinal);
        var queued = operations.Where(o => o.Status == OperationStatus.Queued).Select(o => o.StationCode).ToHashSet(StringComparer.Ordinal);
        var tools = await db.StationTools.AsNoTracking()
            .Where(t => stations.Contains(t.StationCode))
            .Select(t => new { t.StationCode, Changing = t.ChangeDueAt != null, WaitingForTool = t.WaitingSince != null })
            .ToListAsync(cancellationToken);
        var changingTool = tools.Where(t => t.Changing).Select(t => t.StationCode).ToHashSet(StringComparer.Ordinal);
        var waitingForTool = tools.Where(t => t.WaitingForTool).Select(t => t.StationCode).ToHashSet(StringComparer.Ordinal);
        var incoming = (await db.TransportTasks.AsNoTracking()
            .Where(t => stations.Contains(t.ToStationCode) && t.Kind != TransportTaskKind.ToolDelivery
                && (t.Status == TransportTaskStatus.Assigned || t.Status == TransportTaskStatus.Executing))
            .Select(t => t.ToStationCode)
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        // 機台停著等刀時，停機的原因是刀具，不是物料：先判斷。
        foreach (var (task, reason) in waiting.Where(w => w.Task.Kind == TransportTaskKind.ToolDelivery))
        {
            if (waitingForTool.Contains(task.ToStationCode))
            {
                stopped.TryAdd(task.ToStationCode, new StoppedMachine(steps[task.ToStationCode], MachineDelayCause.WaitingForTool, reason, task.Id));
            }
        }

        bool Idle(string station) => steps.ContainsKey(station)
            && !running.Contains(station) && !changingTool.Contains(station) && !waitingForTool.Contains(station);

        foreach (var (task, reason) in waiting.Where(w => w.Task.Kind != TransportTaskKind.ToolDelivery))
        {
            if (Idle(task.FromStationCode) && queued.Contains(task.FromStationCode))
            {
                stopped.TryAdd(task.FromStationCode, new StoppedMachine(steps[task.FromStationCode], MachineDelayCause.OutputNotRemoved, reason, task.Id));
            }

            if (Idle(task.ToStationCode) && !queued.Contains(task.ToStationCode) && !incoming.Contains(task.ToStationCode))
            {
                stopped.TryAdd(task.ToStationCode, new StoppedMachine(steps[task.ToStationCode], MachineDelayCause.WaitingForMaterial, reason, task.Id));
            }
        }

        return stopped;
    }

    /// <summary>
    /// 缺車與離線事故的統計；進行中的區間算到最近一次檢查為止。
    /// <paramref name="vehicles"/> 是車輛的即時狀態，用來回報「此刻哪些車型全部離線」（不管有沒有任務在等）。
    /// </summary>
    public async Task<VehicleShortageDto> GetSummaryAsync(IReadOnlyCollection<VehicleState> vehicles, CancellationToken cancellationToken)
    {
        var stations = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.Code, s => s.Name, cancellationToken);
        var steps = await db.ProcessSteps.AsNoTracking().OrderBy(s => s.Sequence).ToListAsync(cancellationToken);
        var delays = await db.MachineDelays.AsNoTracking()
            .GroupBy(d => new { d.StepSequence, d.Cause, d.Reason })
            .Select(g => new { g.Key.StepSequence, g.Key.Cause, g.Key.Reason, Seconds = g.Sum(d => d.SimulatedSeconds) })
            .ToListAsync(cancellationToken);
        var current = await db.MachineDelays.AsNoTracking()
            .Where(d => d.EndedAt == null)
            .ToDictionaryAsync(d => d.StationCode, d => new { d.Cause, d.Reason }, cancellationToken);
        var waits = await db.VehicleWaits.AsNoTracking()
            .GroupBy(w => w.Reason)
            .Select(g => new { Reason = g.Key, Count = g.Count(), Open = g.Count(w => w.EndedAt == null), Seconds = g.Sum(w => w.SimulatedSeconds) })
            .ToListAsync(cancellationToken);
        var busy = waits.SingleOrDefault(w => w.Reason == ShortageReason.VehiclesBusy);
        var offline = waits.SingleOrDefault(w => w.Reason == ShortageReason.FleetOffline);

        // 有開放派工的車型裡，沒有任何一台在線的。
        var dispatchable = await db.Vehicles.AsNoTracking().Where(v => v.DispatchEnabled).Select(v => new { v.Id, v.Type }).ToListAsync(cancellationToken);
        var online = vehicles.Where(v => v.Connectivity == VehicleConnectivity.Online).Select(v => v.VehicleId).ToHashSet(StringComparer.Ordinal);
        var offlineTypes = dispatchable.GroupBy(v => v.Type)
            .Where(g => !g.Any(v => online.Contains(v.Id)))
            .Select(g => g.Key)
            .OrderBy(t => t)
            .ToList();

        double Busy(int step, MachineDelayCause cause) =>
            delays.Where(d => d.StepSequence == step && d.Cause == cause && d.Reason == ShortageReason.VehiclesBusy).Sum(d => d.Seconds);

        return new VehicleShortageDto
        {
            WaitingTasks = busy?.Open ?? 0,
            WaitCount = busy?.Count ?? 0,
            WaitSeconds = busy?.Seconds ?? 0,
            OfflineVehicleTypes = offlineTypes,
            OfflineWaitingTasks = offline?.Open ?? 0,
            OfflineWaitCount = offline?.Count ?? 0,
            OfflineWaitSeconds = offline?.Seconds ?? 0,
            Steps = steps.Select(step =>
            {
                var material = Busy(step.Sequence, MachineDelayCause.WaitingForMaterial);
                var output = Busy(step.Sequence, MachineDelayCause.OutputNotRemoved);
                var tool = Busy(step.Sequence, MachineDelayCause.WaitingForTool);
                var now = current.GetValueOrDefault(step.StationCode);
                return new StepDelayDto
                {
                    Sequence = step.Sequence,
                    StationCode = step.StationCode,
                    StationName = stations[step.StationCode],
                    DelaySeconds = material + output + tool,
                    WaitingForMaterialSeconds = material,
                    OutputNotRemovedSeconds = output,
                    WaitingForToolSeconds = tool,
                    OfflineSeconds = delays.Where(d => d.StepSequence == step.Sequence && d.Reason == ShortageReason.FleetOffline).Sum(d => d.Seconds),
                    CurrentCause = now?.Cause,
                    CurrentReason = now?.Reason,
                };
            }).ToList(),
        };
    }
}
