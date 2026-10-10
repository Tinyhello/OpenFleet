using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;
using static OpenFleet.Tests.TestDatabase;

namespace OpenFleet.Tests;

/// <summary>
/// 搬運出問題時的恢復流程：物料還在取料站的自動重派；物料在車上的等人工處理；
/// 設備弄丟指令（重啟）由對帳發現。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TransportRecoveryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private MaterialLot _lot = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(postgres);
        await _database.UseSimpleMachiningAsync();
        await using var scope = _database.NewScope();
        _lot = (await scope.Production.CreateBatchAsync(null, null, default)).Single();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Failure_before_pickup_puts_the_task_back_in_the_queue()
    {
        var assignment = await DispatchAsync("agv-a");
        await ApplyAsync(assignment, VehicleEventType.Accepted);
        await ApplyAsync(assignment, VehicleEventType.Failed, "Blocked");

        // 物料還在原料倉：任務回到等待中，可以派給另一台車。
        await using (var scope = _database.NewScope())
        {
            var task = await scope.Db.TransportTasks.SingleAsync();
            Assert.Equal((TransportTaskStatus.Pending, false), (task.Status, task.NeedsAttention));
            Assert.Equal((LotStatus.AwaitingTransport, "raw-store"), await LotAsync(scope));
        }

        var second = await DispatchAsync("agv-b");
        Assert.Equal(("agv-b", 2), (second.VehicleId, second.Attempt));
    }

    [Fact]
    public async Task Failure_with_the_load_on_board_waits_for_an_operator()
    {
        var assignment = await DispatchAsync("agv-a");
        await ApplyAsync(assignment, VehicleEventType.Accepted);
        await ApplyAsync(assignment, VehicleEventType.Loaded);
        await ApplyAsync(assignment, VehicleEventType.Failed, "Motor fault");

        // 物料在車上：系統不知道實際狀況，不自動重派，也不釋放物料。
        await using (var scope = _database.NewScope())
        {
            var task = await scope.Db.TransportTasks.SingleAsync();
            Assert.Equal((TransportTaskStatus.Failed, true), (task.Status, task.NeedsAttention));
            Assert.Equal((LotStatus.InTransit, "vehicle:agv-a"), await LotAsync(scope));
            Assert.Equal(0, await scope.Transport.DispatchPendingAsync([Online("agv-b")], default));
        }

        // 人工放回取料站：物料回到原料倉，另開一趟新的搬運；原本那筆留著失敗與處理的紀錄。
        await ResolveAsync(TaskResolution.ReturnToSource);
        await using (var scope = _database.NewScope())
        {
            var tasks = await scope.Db.TransportTasks.OrderBy(t => t.CreatedAt).ToListAsync();
            Assert.Equal(new[] { TransportTaskStatus.Failed, TransportTaskStatus.Pending }, tasks.Select(t => t.Status));
            Assert.Equal((TaskResolution.ReturnToSource, false), (tasks[0].Resolution, tasks[0].NeedsAttention));
            Assert.Equal((LotStatus.AwaitingTransport, "raw-store"), await LotAsync(scope));
            Assert.Equal("ManualReturn", (await scope.Db.InventoryMovements.OrderBy(m => m.Id).LastAsync()).Reason);

            // 同一筆不能處理第二次。
            await Assert.ThrowsAsync<RequestValidationException>(() => scope.Transport.ResolveAsync(tasks[0].Id, TaskResolution.ReturnToSource, [], default));
        }

        Assert.Equal("agv-b", (await DispatchAsync("agv-b")).VehicleId);
    }

    [Fact]
    public async Task Operator_can_confirm_that_the_load_was_delivered_by_hand()
    {
        var assignment = await DispatchAsync("agv-a");
        await ApplyAsync(assignment, VehicleEventType.Accepted);
        await ApplyAsync(assignment, VehicleEventType.Loaded);
        await ApplyAsync(assignment, VehicleEventType.Failed, "Motor fault");

        await ResolveAsync(TaskResolution.DeliverToDestination);

        // 視同送達：批次在粗加工排隊，下一輪就開始加工。
        await using var scope = _database.NewScope();
        Assert.Equal((LotStatus.Queued, "rough"), await LotAsync(scope));
        Assert.Equal(1, await scope.Db.TransportTasks.CountAsync());
        Assert.True(await scope.Production.AdvanceOperationsAsync(default));
        Assert.Equal(OperationStatus.Running, (await scope.Db.Operations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Command_the_vehicle_no_longer_reports_is_treated_as_failed_after_a_grace_period()
    {
        var assignment = await DispatchAsync("agv-a");
        await ApplyAsync(assignment, VehicleEventType.Accepted);
        var working = Online("agv-a");
        working.ActiveCommandId = assignment.CommandId;
        var restarted = Online("agv-a");

        // 設備有在執行：沒事。離線的車無法判斷：也維持原狀。
        _database.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await ReconcileAsync(working));
        var offline = Online("agv-a");
        offline.Connectivity = VehicleConnectivity.Offline;
        Assert.Equal(0, await ReconcileAsync(offline));
        _database.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await ReconcileAsync(offline));

        // 設備重啟後不再回報這道指令：要持續超過寬限時間才認定（完成的回報可能比遙測晚到）。
        Assert.Equal(0, await ReconcileAsync(restarted));
        _database.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await ReconcileAsync(restarted));

        // 中間又回報有在執行：重新計算。
        Assert.Equal(0, await ReconcileAsync(working));
        _database.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await ReconcileAsync(restarted));
        _database.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await ReconcileAsync(restarted));

        // 還沒取料：任務回到等待中。
        await using var scope = _database.NewScope();
        Assert.Equal(TransportTaskStatus.Pending, (await scope.Db.TransportTasks.SingleAsync()).Status);
        Assert.Equal(AssignmentStatus.Failed, (await scope.Db.Assignments.SingleAsync()).Status);
    }

    [Fact]
    public async Task Command_that_never_reached_the_vehicle_is_dispatched_again()
    {
        // 指令逾時沒有任何回覆（待確認），而車輛在線、也沒有在執行它：指令遺失，重新派工。
        await DispatchAsync("agv-a");
        _database.Time.Advance(_database.Fleet.CommandTimeToLive + TimeSpan.FromSeconds(1));
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(1, await scope.Transport.MarkExpiredAssignmentsAsync(default));
        }

        Assert.Equal(0, await ReconcileAsync(Online("agv-a")));
        _database.Time.Advance(_database.Fleet.LostCommandGrace);
        Assert.Equal(1, await ReconcileAsync(Online("agv-a")));
        Assert.Equal(2, (await DispatchAsync("agv-a")).Attempt);
    }

    [Fact]
    public async Task Task_stuck_on_an_offline_vehicle_can_be_resolved_by_an_operator_but_not_while_the_vehicle_is_online()
    {
        var assignment = await DispatchAsync("agv-a");
        await ApplyAsync(assignment, VehicleEventType.Accepted);
        await ApplyAsync(assignment, VehicleEventType.Loaded);
        Guid taskId;
        await using (var scope = _database.NewScope())
        {
            taskId = (await scope.Db.TransportTasks.SingleAsync()).Id;
            await Assert.ThrowsAsync<RequestValidationException>(() =>
                scope.Transport.ResolveAsync(taskId, TaskResolution.ReturnToSource, [Online("agv-a")], default));
        }

        await using (var scope = _database.NewScope())
        {
            await scope.Transport.ResolveAsync(taskId, TaskResolution.ReturnToSource, [], default);
            Assert.Equal((LotStatus.AwaitingTransport, "raw-store"), await LotAsync(scope));
            Assert.Equal(AssignmentStatus.Failed, (await scope.Db.Assignments.SingleAsync()).Status);
        }

        // 車輛空出來了，新的那一趟可以再派給它。
        Assert.Equal(1, (await DispatchAsync("agv-a")).Attempt);
    }

    private async Task<(LotStatus, string)> LotAsync(Scope scope)
    {
        var lot = await scope.Db.Lots.AsNoTracking().SingleAsync(l => l.Id == _lot.Id);
        return (lot.Status, lot.StationCode ?? lot.Location);
    }

    private async Task<Assignment> DispatchAsync(string vehicleId)
    {
        await using var scope = _database.NewScope();
        Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online(vehicleId)], default));
        return await scope.Db.Assignments.AsNoTracking().SingleAsync(a => a.Status == AssignmentStatus.Dispatched);
    }

    private async Task ApplyAsync(Assignment assignment, VehicleEventType type, string? reason = null)
    {
        await using var scope = _database.NewScope();
        Assert.True(await scope.Events.ProcessAsync(assignment.VehicleId, Event(assignment, type, reason), default));
    }

    private async Task<int> ReconcileAsync(VehicleState vehicle)
    {
        await using var scope = _database.NewScope();
        return await scope.Transport.ReconcileAsync([vehicle], default);
    }

    private async Task ResolveAsync(TaskResolution resolution)
    {
        await using var scope = _database.NewScope();
        var task = await scope.Db.TransportTasks.AsNoTracking().SingleAsync(t => t.Status == TransportTaskStatus.Failed && t.Resolution == null);
        await scope.Transport.ResolveAsync(task.Id, resolution, [], default);
    }
}
