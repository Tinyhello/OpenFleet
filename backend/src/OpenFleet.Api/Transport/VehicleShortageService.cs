using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Production;
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
public sealed class VehicleShortageService(OpenFleetDbContext db, TimeProvider time, ILogger<VehicleShortageService> logger)
{
    /// <summary><paramref name="waiting"/> 依派工優先順序排列。</summary>
    public async Task RecordAsync(IReadOnlyList<WaitingTask> waiting, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        var openWaits = await db.VehicleWaits.Where(w => w.EndedAt == null).ToListAsync(cancellationToken);
        var openDelays = await db.MachineDelays.Where(d => d.EndedAt == null).ToListAsync(cancellationToken);
        if (waiting.Count == 0 && openWaits.Count == 0 && openDelays.Count == 0)
        {
            return;
        }

        var newWaits = waiting.ToDictionary(w => w.Task.Id);
        foreach (var wait in openWaits)
        {
            wait.Accrue(now);
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
            delay.Accrue(now);
            if (stopped.TryGetValue(delay.MachineCode, out var current) && current.Cause == delay.Cause && current.Reason == delay.Reason)
            {
                stopped.Remove(delay.MachineCode);
            }
            else
            {
                delay.Close(now);
                logger.LogInformation("Machine {Machine}: delay ended after {Seconds:0.#}s ({Cause}, {Reason}).",
                    delay.MachineCode, delay.SimulatedSeconds, delay.Cause, delay.Reason);
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

        foreach (var (machineCode, machine) in stopped)
        {
            db.MachineDelays.Add(MachineDelay.Open(machine.Station, machineCode, machine.Step, machine.Cause, machine.Reason, machine.TaskId, now));
            logger.LogInformation("Machine {Machine} is stopped: {Cause} ({Reason}).", machineCode, machine.Cause, machine.Reason);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record StoppedMachine(string Station, int Step, MachineDelayCause Cause, ShortageReason Reason, Guid TaskId);

    /// <summary>
    /// 等車的任務讓哪些機台停了下來（以機台計，同一站的兩台車床各自算）。機台正在加工、換刀或停機等刀就不算閒置。
    /// 等刀：機台已經到刀具壽命、停機等新刀，而送給它的刀叫不到車。
    /// 成品沒被搬走：取料站是機台，那台機台做完的框沒被搬走，而且站上還有框排隊、別台空著的機台接不完。
    /// 等不到料：卸料站是機台，閒置的機台比站上排隊與正在送來的框多；叫不到車的框有幾框，就算幾台在等料。
    /// </summary>
    private async Task<Dictionary<string, StoppedMachine>> FindStoppedMachinesAsync(
        IReadOnlyList<WaitingTask> waiting, CancellationToken cancellationToken)
    {
        var stopped = new Dictionary<string, StoppedMachine>(StringComparer.Ordinal);
        if (waiting.Count == 0)
        {
            return stopped;
        }

        // 只看逐件工序的機台。整爐工序本來就要等料累積到下限，站內沒有料不代表是缺車造成的停機。
        var steps = await db.ProcessSteps.AsNoTracking()
            .Where(s => s.Kind == ProcessStepKind.Piece)
            .ToDictionaryAsync(s => s.StationCode, s => s.Sequence, cancellationToken);
        var stations = waiting.SelectMany(w => new[] { w.Task.FromStationCode, w.Task.ToStationCode }).Where(steps.ContainsKey).Distinct().ToList();
        if (stations.Count == 0)
        {
            return stopped;
        }

        var machines = await db.Machines.AsNoTracking()
            .Where(m => stations.Contains(m.StationCode))
            .OrderBy(m => m.Number)
            .ToListAsync(cancellationToken);
        var operations = await db.Operations.AsNoTracking()
            .Where(o => stations.Contains(o.StationCode) && o.Status != OperationStatus.Completed)
            .Select(o => new { o.StationCode, o.MachineCode, o.Status })
            .ToListAsync(cancellationToken);
        var running = operations.Where(o => o.Status == OperationStatus.Running && o.MachineCode != null)
            .Select(o => o.MachineCode!).ToHashSet(StringComparer.Ordinal);
        var machineCodes = machines.Select(m => m.Code).ToList();
        var tools = await db.MachineTools.AsNoTracking()
            .Where(t => machineCodes.Contains(t.MachineCode))
            .Select(t => new { t.MachineCode, Changing = t.ChangeDueAt != null, WaitingForTool = t.WaitingSince != null })
            .ToListAsync(cancellationToken);
        var changingTool = tools.Where(t => t.Changing).Select(t => t.MachineCode).ToHashSet(StringComparer.Ordinal);
        var waitingForTool = tools.Where(t => t.WaitingForTool).Select(t => t.MachineCode).ToHashSet(StringComparer.Ordinal);
        var blocked = (await MachineQueries.BlockedMachinesAsync(db, stations, machines, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var incoming = await db.TransportTasks.AsNoTracking()
            .Where(t => stations.Contains(t.ToStationCode) && t.Kind != TransportTaskKind.ToolDelivery
                && (t.Status == TransportTaskStatus.Assigned || t.Status == TransportTaskStatus.Executing))
            .Select(t => t.ToStationCode)
            .ToListAsync(cancellationToken);

        int Queued(string station) => operations.Count(o => o.StationCode == station && o.Status == OperationStatus.Queued);
        bool Idle(Machine machine) =>
            !running.Contains(machine.Code) && !changingTool.Contains(machine.Code) && !waitingForTool.Contains(machine.Code);
        void Stop(Machine machine, MachineDelayCause cause, ShortageReason reason, Guid taskId) =>
            stopped.TryAdd(machine.Code, new StoppedMachine(machine.StationCode, steps[machine.StationCode], cause, reason, taskId));

        // 機台停著等刀時，停機的原因是刀具，不是物料：先判斷。
        foreach (var (task, reason) in waiting.Where(w => w.Task.Kind == TransportTaskKind.ToolDelivery))
        {
            var target = task.MachineCode ?? Machine.CodeOf(task.ToStationCode, 1);
            if (waitingForTool.Contains(target) && machines.SingleOrDefault(m => m.Code == target) is { } machine)
            {
                Stop(machine, MachineDelayCause.WaitingForTool, reason, task.Id);
            }
        }

        var material = waiting.Where(w => w.Task.Kind != TransportTaskKind.ToolDelivery).ToList();
        var finishedOn = await MachineQueries.FinishedOnAsync(db, material.Select(w => w.Task.LotId).OfType<Guid>().ToList(), cancellationToken);

        // 成品沒被搬走：做完這一框的機台被它卡住，而站上還有框排隊、沒有空著的機台可以接（每一框只算一台）。
        var work = stations.ToDictionary(
            s => s, s => Queued(s) - machines.Count(m => m.StationCode == s && Idle(m) && !blocked.Contains(m.Code)), StringComparer.Ordinal);
        foreach (var (task, reason) in material)
        {
            var source = machines.Where(m => m.StationCode == task.FromStationCode).ToList();
            var maker = source.Count == 1 ? source[0]
                : task.LotId is { } lotId && finishedOn.TryGetValue(lotId, out var code) ? source.SingleOrDefault(m => m.Code == code)
                : null;
            if (maker is not null && Idle(maker) && blocked.Contains(maker.Code) && !stopped.ContainsKey(maker.Code) && work[maker.StationCode] > 0)
            {
                Stop(maker, MachineDelayCause.OutputNotRemoved, reason, task.Id);
                work[maker.StationCode]--;
            }
        }

        // 等不到料：每一站閒置的機台扣掉排隊與正在送來的框，就是沒有東西可做的台數；叫不到車的框有幾框，最多就算幾台在等料。
        foreach (var group in material.GroupBy(w => w.Task.ToStationCode).Where(g => steps.ContainsKey(g.Key)))
        {
            var idle = machines.Where(m => m.StationCode == group.Key && Idle(m)).ToList();
            var starving = idle.Count - Queued(group.Key) - incoming.Count(s => s == group.Key);
            foreach (var (machine, (task, reason)) in idle.Where(m => !stopped.ContainsKey(m.Code)).Zip(group).Take(Math.Max(0, starving)))
            {
                Stop(machine, MachineDelayCause.WaitingForMaterial, reason, task.Id);
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
        var steps = await db.ProcessSteps.AsNoTracking()
            .Where(s => s.Kind == ProcessStepKind.Piece)
            .OrderBy(s => s.Sequence)
            .ToListAsync(cancellationToken);
        var delays = await db.MachineDelays.AsNoTracking()
            .GroupBy(d => new { d.StepSequence, d.Cause, d.Reason })
            .Select(g => new { g.Key.StepSequence, g.Key.Cause, g.Key.Reason, Seconds = g.Sum(d => d.SimulatedSeconds) })
            .ToListAsync(cancellationToken);
        // 同一站好幾台都停著時，顯示編號最小的那一台的原因。
        var current = (await db.MachineDelays.AsNoTracking()
                .Where(d => d.EndedAt == null)
                .OrderBy(d => d.MachineCode)
                .Select(d => new { d.StationCode, d.Cause, d.Reason })
                .ToListAsync(cancellationToken))
            .GroupBy(d => d.StationCode)
            .ToDictionary(g => g.Key, g => g.First());
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
