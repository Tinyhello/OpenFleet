using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;
using static OpenFleet.Tests.TestDatabase;

namespace OpenFleet.Tests;

/// <summary>
/// P2 的一致性驗證：直接對 PostgreSQL 測試派工、事件處理與加工推進，時間由 FakeTimeProvider 控制。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MaterialFlowTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await TestDatabase.CreateAsync(postgres);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Creating_a_lot_records_receipt_and_requests_transport_to_the_first_step()
    {
        var lot = await CreateLotAsync();

        await using var scope = _database.NewScope();
        var task = await scope.Db.TransportTasks.SingleAsync();
        var movement = await scope.Db.InventoryMovements.SingleAsync();

        Assert.Equal((LotStatus.AwaitingTransport, "raw-store", 1), (lot.Status, lot.StationCode, lot.CurrentStepSequence!.Value));
        Assert.Equal(("raw-store", "rough", TransportTaskStatus.Pending, 30), (task.FromStationCode, task.ToStationCode, task.Status, task.Quantity));
        Assert.Equal((null, "station:raw-store", "Received"), (movement.From, movement.To, movement.Reason));
    }

    [Fact]
    public async Task Dispatch_writes_assignment_task_state_and_command_in_one_transaction()
    {
        await CreateLotAsync();

        var assignment = await DispatchAsync(Online("agv-a"));

        await using var scope = _database.NewScope();
        var task = await scope.Db.TransportTasks.SingleAsync();
        var outbox = await scope.Db.OutboxMessages.SingleAsync();

        Assert.Equal(TransportTaskStatus.Assigned, task.Status);
        Assert.Equal(("agv-a", 1, AssignmentStatus.Dispatched), (assignment.VehicleId, assignment.Attempt, assignment.Status));
        Assert.EndsWith("/vehicles/agv-a/commands", outbox.Topic);
        Assert.Contains(assignment.Id.ToString(), outbox.Payload);
        Assert.Contains("\"commandType\":\"transport\"", outbox.Payload);
        Assert.Null(outbox.PublishedAt);
    }

    [Fact]
    public async Task Only_online_large_vehicles_without_an_active_assignment_are_dispatched()
    {
        await CreateLotAsync();
        await CreateLotAsync();

        await using (var scope = _database.NewScope())
        {
            // 小型車只送刀具；離線的車不派。
            var offline = Online("agv-b");
            offline.Connectivity = VehicleConnectivity.Offline;
            Assert.Equal(0, await scope.Transport.DispatchPendingAsync([Online("agv-s"), offline], default));
        }

        // 目的站（粗加工）容量 30：第一批占滿後，第二批即使有空車也要等待。
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online("agv-a"), Online("agv-b")], default));
            Assert.Equal(0, await scope.Transport.DispatchPendingAsync([Online("agv-a"), Online("agv-b")], default));
            Assert.Equal(1, await scope.Db.TransportTasks.CountAsync(t => t.Status == TransportTaskStatus.Pending));
        }
    }

    [Fact]
    public async Task Nearest_available_vehicle_is_chosen()
    {
        await CreateLotAsync();

        // 取料點（原料倉）在 (-16, -7.6)。
        var assignment = await DispatchAsync(Online("agv-a", x: 10), Online("agv-b", x: -15, z: -6));

        Assert.Equal("agv-b", assignment.VehicleId);
    }

    [Fact]
    public async Task Database_rejects_a_second_active_assignment_for_the_same_vehicle()
    {
        // 兩個「工作者」各自讀到同一台車是空的，分別把不同任務派給它。
        await CreateLotAsync();
        await CreateLotAsync();

        await using var first = _database.NewScope();
        await using var second = _database.NewScope();
        var tasks = await first.Db.TransportTasks.OrderBy(t => t.CreatedAt).Select(t => t.Id).ToListAsync();

        var taskA = await first.Db.TransportTasks.SingleAsync(t => t.Id == tasks[0]);
        first.Db.Assignments.Add(taskA.Assign("agv-a", 1, _database.Time.GetUtcNow(), TimeSpan.FromSeconds(10)));
        var taskB = await second.Db.TransportTasks.SingleAsync(t => t.Id == tasks[1]);
        second.Db.Assignments.Add(taskB.Assign("agv-a", 1, _database.Time.GetUtcNow(), TimeSpan.FromSeconds(10)));

        await first.Db.SaveChangesAsync();
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => second.Db.SaveChangesAsync());

        Assert.Contains("UX_Assignments_ActiveVehicle", exception.InnerException!.Message);
        await using var check = _database.NewScope();
        Assert.Equal(1, await check.Db.Assignments.CountAsync());
        Assert.Equal(TransportTaskStatus.Pending, (await check.Db.TransportTasks.SingleAsync(t => t.Id == tasks[1])).Status);
    }

    [Fact]
    public async Task Stale_write_to_the_same_lot_fails_with_a_concurrency_exception()
    {
        await CreateLotAsync();

        await using var first = _database.NewScope();
        await using var second = _database.NewScope();
        var mine = await first.Db.Lots.SingleAsync();
        var theirs = await second.Db.Lots.SingleAsync();

        mine.LoadOnto("agv-a", _database.Time.GetUtcNow());
        await first.Db.SaveChangesAsync();

        // 第二個工作者手上是舊版本（xmin 已改變）：UPDATE 影響 0 列，EF Core 丟出並行例外而不是覆蓋別人的變更。
        theirs.LoadOnto("agv-b", _database.Time.GetUtcNow());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.Db.SaveChangesAsync());

        await using var check = _database.NewScope();
        Assert.Equal("agv-a", (await check.Db.Lots.SingleAsync()).VehicleId);
    }

    [Fact]
    public async Task Vehicle_events_move_the_lot_through_pickup_and_delivery()
    {
        var lot = await CreateLotAsync();
        var assignment = await DispatchAsync(Online("agv-a"));

        await ApplyAsync(assignment, VehicleEventType.Accepted);
        Assert.Equal((TransportTaskStatus.Executing, LotStatus.AwaitingTransport, "raw-store"), await StateAsync(lot.Id));

        await ApplyAsync(assignment, VehicleEventType.Loaded);
        Assert.Equal((TransportTaskStatus.Executing, LotStatus.InTransit, "vehicle:agv-a"), await StateAsync(lot.Id));

        await ApplyAsync(assignment, VehicleEventType.Completed);
        Assert.Equal((TransportTaskStatus.Completed, LotStatus.Queued, "rough"), await StateAsync(lot.Id));

        await using var scope = _database.NewScope();
        Assert.Equal(new[] { "Received", "Loaded", "Unloaded" }, await scope.Db.InventoryMovements.OrderBy(m => m.Id).Select(m => m.Reason).ToListAsync());
        Assert.Equal(OperationStatus.Queued, (await scope.Db.Operations.SingleAsync()).Status);
        Assert.Empty(await scope.Transport.GetBusyVehicleIdsAsync(default));
    }

    [Fact]
    public async Task Redelivered_event_has_no_second_effect()
    {
        await CreateLotAsync();
        var assignment = await DispatchAsync(Online("agv-a"));
        await ApplyAsync(assignment, VehicleEventType.Accepted);
        var loaded = Event(assignment, VehicleEventType.Loaded);

        await using (var scope = _database.NewScope())
        {
            Assert.True(await scope.Events.ProcessAsync("agv-a", loaded, default));
        }

        await using (var scope = _database.NewScope())
        {
            Assert.False(await scope.Events.ProcessAsync("agv-a", loaded, default));
            Assert.Equal(1, await scope.Db.InventoryMovements.CountAsync(m => m.Reason == "Loaded"));
            Assert.Equal(2, await scope.Db.InboxMessages.CountAsync());
        }
    }

    [Fact]
    public async Task Out_of_order_or_foreign_events_are_ignored()
    {
        var lot = await CreateLotAsync();
        var assignment = await DispatchAsync(Online("agv-a"));

        // 還沒取料就回報完成；別台車回報這筆派工。
        await ApplyAsync(assignment, VehicleEventType.Completed);
        await using (var scope = _database.NewScope())
        {
            await scope.Events.ProcessAsync("agv-b", Event(assignment, VehicleEventType.Accepted), default);
        }

        Assert.Equal((TransportTaskStatus.Assigned, LotStatus.AwaitingTransport, "raw-store"), await StateAsync(lot.Id));
    }

    [Fact]
    public async Task Rejected_command_requeues_the_task_until_attempts_run_out()
    {
        await CreateLotAsync();

        for (var attempt = 1; attempt <= _database.Fleet.MaxDispatchAttempts; attempt++)
        {
            var assignment = await DispatchAsync(Online("agv-a"));
            Assert.Equal(attempt, assignment.Attempt);
            await ApplyAsync(assignment, VehicleEventType.Rejected, "Busy");

            await using var scope = _database.NewScope();
            var expected = attempt < _database.Fleet.MaxDispatchAttempts ? TransportTaskStatus.Pending : TransportTaskStatus.Failed;
            Assert.Equal(expected, (await scope.Db.TransportTasks.SingleAsync()).Status);
        }

        await using var final = _database.NewScope();
        var events = await final.Db.TaskEvents.OrderBy(e => e.Id).Select(e => e.ToStatus).ToListAsync();
        Assert.Equal(TransportTaskStatus.Failed, events[^1]);
        Assert.Equal(3, events.Count(s => s == TransportTaskStatus.Assigned));
    }

    [Fact]
    public async Task Command_without_reply_becomes_unconfirmed_and_keeps_the_vehicle_reserved()
    {
        await CreateLotAsync();
        var assignment = await DispatchAsync(Online("agv-a"));

        _database.Time.Advance(_database.Fleet.CommandTimeToLive + TimeSpan.FromSeconds(1));
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(1, await scope.Transport.MarkExpiredAssignmentsAsync(default));
            Assert.Equal(AssignmentStatus.Unconfirmed, (await scope.Db.Assignments.SingleAsync()).Status);

            // 待確認期間不改派：車輛仍被占用，任務也不會回到待派工。
            Assert.Equal(new[] { "agv-a" }, await scope.Transport.GetBusyVehicleIdsAsync(default));
            Assert.Equal(0, await scope.Transport.DispatchPendingAsync([Online("agv-a"), Online("agv-b")], default));
        }

        // 設備其實有收到，只是回報晚到：照常繼續。
        await ApplyAsync(assignment, VehicleEventType.Accepted);
        await using var after = _database.NewScope();
        Assert.Equal(AssignmentStatus.Accepted, (await after.Db.Assignments.SingleAsync()).Status);
        Assert.Equal(TransportTaskStatus.Executing, (await after.Db.TransportTasks.SingleAsync()).Status);
    }

    [Fact]
    public async Task Machine_processes_the_whole_lot_for_the_configured_time_then_requests_the_next_transport()
    {
        var lot = await CreateLotAsync();
        await DeliverAsync("agv-a");

        await AdvanceOperationsAsync();
        Assert.Equal((TransportTaskStatus.Completed, LotStatus.Processing, "rough"), await StateAsync(lot.Id));

        // 還差 1 秒：不能提早放行。
        _database.Time.Advance(TimeSpan.FromSeconds(9));
        await AdvanceOperationsAsync();
        Assert.Equal(LotStatus.Processing, (await StateAsync(lot.Id)).Lot);

        _database.Time.Advance(TimeSpan.FromSeconds(1));
        await AdvanceOperationsAsync();

        await using var scope = _database.NewScope();
        var stored = await scope.Db.Lots.SingleAsync();
        var next = await scope.Db.TransportTasks.SingleAsync(t => t.Status == TransportTaskStatus.Pending);
        Assert.Equal((LotStatus.AwaitingTransport, "rough", 2), (stored.Status, stored.StationCode, stored.CurrentStepSequence!.Value));
        Assert.Equal(("rough", "fine", TransportTaskKind.WipTransfer), (next.FromStationCode, next.ToStationCode, next.Kind));
    }

    [Fact]
    public async Task Machine_does_not_start_the_next_lot_until_the_finished_one_is_picked_up()
    {
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();

        // 第一批加工中時，第二批送到機邊排隊。
        await CreateLotAsync();
        await DeliverAsync("agv-b");
        await AdvanceOperationsAsync();
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(1, await scope.Db.Operations.CountAsync(o => o.Status == OperationStatus.Running));
            Assert.Equal(1, await scope.Db.Operations.CountAsync(o => o.Status == OperationStatus.Queued));
        }

        // 第一批完成但還沒被搬走：第二批不能開始。
        _database.Time.Advance(TimeSpan.FromSeconds(10));
        await AdvanceOperationsAsync();
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(0, await scope.Db.Operations.CountAsync(o => o.Status == OperationStatus.Running));
        }

        // 第一批取走後，第二批開始加工。
        var pickup = await DispatchAsync(Online("agv-a"));
        await ApplyAsync(pickup, VehicleEventType.Accepted);
        await ApplyAsync(pickup, VehicleEventType.Loaded);
        await AdvanceOperationsAsync();
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(1, await scope.Db.Operations.CountAsync(o => o.Status == OperationStatus.Running));
        }
    }

    [Fact]
    public async Task Last_step_sends_the_lot_to_the_buffer_where_it_is_staged()
    {
        var lot = await CreateLotAsync();

        foreach (var station in new[] { "rough", "fine" })
        {
            await DeliverAsync("agv-a");
            await AdvanceOperationsAsync();
            _database.Time.Advance(TimeSpan.FromSeconds(10));
            await AdvanceOperationsAsync();
            Assert.Equal((LotStatus.AwaitingTransport, station), ((await StateAsync(lot.Id)).Lot, (await StateAsync(lot.Id)).Location));
        }

        await DeliverAsync("agv-a");

        Assert.Equal((TransportTaskStatus.Completed, LotStatus.Staged, "heat"), await StateAsync(lot.Id));
        await using var scope = _database.NewScope();
        Assert.Equal(7, await scope.Db.InventoryMovements.CountAsync());
        Assert.Equal(2, await scope.Db.Operations.CountAsync(o => o.Status == OperationStatus.Completed));
    }

    [Fact]
    public async Task Settings_can_be_changed_and_are_validated()
    {
        await using var scope = _database.NewScope();
        var settings = await scope.Production.GetSettingsAsync(default);
        Assert.Equal((30, 30, 2), (settings.LotQuantity, settings.MaxLotQuantity, settings.Steps.Count));

        settings.LotQuantity = 12;
        settings.Steps[0].DurationSeconds = 25;
        var updated = await scope.Production.UpdateSettingsAsync(settings, default);
        Assert.Equal((12, 25, 10), (updated.LotQuantity, updated.Steps[0].DurationSeconds, updated.Steps[1].DurationSeconds));
        Assert.Equal(12, (await scope.Production.CreateLotAsync(null, default)).Quantity);

        // 待加工料容量是 30：不拆批時批量不能超過它。
        settings.LotQuantity = 31;
        await Assert.ThrowsAsync<RequestValidationException>(() => scope.Production.UpdateSettingsAsync(settings, default));
        settings.LotQuantity = 12;
        settings.Steps[1].DurationSeconds = 0;
        await Assert.ThrowsAsync<RequestValidationException>(() => scope.Production.UpdateSettingsAsync(settings, default));
        await Assert.ThrowsAsync<RequestValidationException>(() => scope.Production.CreateLotAsync(0, default));
    }

    [Fact]
    public async Task Database_refuses_a_lot_that_is_in_two_places_or_nowhere()
    {
        var lot = await CreateLotAsync();

        await using var scope = _database.NewScope();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => scope.Db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Lots" SET "VehicleId" = 'agv-a' WHERE "Id" = {lot.Id}"""));

        Assert.Contains("CK_Lots_OneLocation", exception.Message);
    }

    private async Task<MaterialLot> CreateLotAsync()
    {
        await using var scope = _database.NewScope();
        var lot = await scope.Production.CreateLotAsync(null, default);
        // 讓建立時間有先後，派工順序才是確定的。
        _database.Time.Advance(TimeSpan.FromMilliseconds(10));
        return lot;
    }

    private async Task<Assignment> DispatchAsync(params VehicleState[] online)
    {
        await using var scope = _database.NewScope();
        Assert.Equal(1, await scope.Transport.DispatchPendingAsync(online, default));
        return await scope.Db.Assignments.AsNoTracking().OrderByDescending(a => a.AssignedAt).ThenByDescending(a => a.Attempt).FirstAsync();
    }

    /// <summary>派工並走完接受、取料、卸料。</summary>
    private async Task DeliverAsync(string vehicleId)
    {
        var assignment = await DispatchAsync(Online(vehicleId));
        await ApplyAsync(assignment, VehicleEventType.Accepted);
        await ApplyAsync(assignment, VehicleEventType.Loaded);
        await ApplyAsync(assignment, VehicleEventType.Completed);
    }

    private async Task ApplyAsync(Assignment assignment, VehicleEventType type, string? reason = null)
    {
        await using var scope = _database.NewScope();
        Assert.True(await scope.Events.ProcessAsync(assignment.VehicleId, Event(assignment, type, reason), default));
        _database.Time.Advance(TimeSpan.FromMilliseconds(10));
    }

    private async Task AdvanceOperationsAsync()
    {
        await using var scope = _database.NewScope();
        await scope.Production.AdvanceOperationsAsync(default);
    }

    /// <summary>批次最新一趟搬運的狀態、批次狀態與位置（站點代碼或 vehicle:xxx）。</summary>
    private async Task<(TransportTaskStatus Task, LotStatus Lot, string Location)> StateAsync(Guid lotId)
    {
        await using var scope = _database.NewScope();
        var lot = await scope.Db.Lots.SingleAsync(l => l.Id == lotId);
        var task = await scope.Db.TransportTasks.Where(t => t.LotId == lotId).OrderByDescending(t => t.CreatedAt).FirstAsync();
        return (task.Status, lot.Status, lot.StationCode ?? lot.Location);
    }
}
