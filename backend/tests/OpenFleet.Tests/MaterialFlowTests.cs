using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Production;
using OpenFleet.Api.Transport;
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

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(postgres);
        // 以下測試的時間點都以每件 2 秒、換刀 20 秒計算。
        await _database.UseSimpleMachiningAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Creating_a_lot_records_receipt_and_requests_transport_to_the_first_step()
    {
        var lot = await CreateLotAsync();

        await using var scope = _database.NewScope();
        var task = await scope.Db.TransportTasks.SingleAsync();
        var movement = await scope.Db.InventoryMovements.SingleAsync();

        Assert.Equal((LotStatus.AwaitingTransport, "raw-store", 1), (lot.Status, lot.StationCode, lot.CurrentStepSequence!.Value));
        Assert.Equal(("raw-store", "rough", TransportTaskStatus.Pending, 30), (task.FromStationCode, TaskTarget.Of(task), task.Status, task.Quantity));
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
        await CreateLotAsync();

        await using (var scope = _database.NewScope())
        {
            // 小型車只送刀具；離線的車不派。
            var offline = Online("agv-b");
            offline.Connectivity = VehicleConnectivity.Offline;
            Assert.Equal(0, await scope.Transport.DispatchPendingAsync([Online("agv-s"), offline], default));
        }

        // 目的站（粗加工）兩台車床、容量 60：前兩批占滿後，第三批即使有空車也要等待。
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(2, await scope.Transport.DispatchPendingAsync([Online("agv-a"), Online("agv-b"), Online("agv-c")], default));
            Assert.Equal(0, await scope.Transport.DispatchPendingAsync([Online("agv-a"), Online("agv-b"), Online("agv-c")], default));
            Assert.Equal(1, await scope.Db.TransportTasks.CountAsync(t => t.Status == TransportTaskStatus.Pending));
        }
    }

    [Fact]
    public async Task Vehicle_with_the_shortest_drive_along_the_road_network_is_chosen()
    {
        await CreateLotAsync();

        // 取料點（原料倉）在 (-20, -9.4)。agv-a 的直線距離比較近，但它在充電車位裡，要先繞出來；
        // agv-b 在外圈車道上，沿路網開過去比較短。
        var assignment = await DispatchAsync(Online("agv-a", x: -10.3, z: 0.2), Online("agv-b", x: -4, z: -7));

        Assert.Equal("agv-b", assignment.VehicleId);
    }

    [Fact]
    public async Task Finished_work_is_moved_before_new_material_is_brought_in()
    {
        // 第一批加工完成、等著搬走；之後才建立第二批（補料任務比較早建立也一樣排後面）。
        await ConfigureAsync(s => s.LotQuantity = 15);
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await CreateLotAsync();
        await AdvanceOperationsAsync();
        _database.Time.Advance(TimeSpan.FromSeconds(30));
        await AdvanceOperationsAsync();

        await using var scope = _database.NewScope();
        var pending = await scope.Db.TransportTasks.Where(t => t.Status == TransportTaskStatus.Pending).OrderBy(t => t.CreatedAt).ToListAsync();
        Assert.Equal(new[] { TransportTaskKind.Replenishment, TransportTaskKind.WipTransfer }, pending.Select(t => t.Kind));

        // 只有一台車：先搬完成品（含各工序做完的半成品），補料等下一輪。
        Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online("agv-a")], default));
        var assigned = await scope.Db.TransportTasks.SingleAsync(t => t.Status == TransportTaskStatus.Assigned);
        Assert.Equal((TransportTaskKind.WipTransfer, "rough-1", "fine-1"), (assigned.Kind, assigned.FromStationCode, TaskTarget.Of(assigned)));
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
        Assert.Equal((TransportTaskStatus.Completed, LotStatus.Queued, "rough-1"), await StateAsync(lot.Id));

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
            await ApplyAsync(assignment, VehicleEventType.Rejected, "Blocked");

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
    public async Task Busy_vehicle_is_not_a_failure_and_rests_while_another_vehicle_takes_the_task()
    {
        // 示範重設之後，車可能還在做後端已經不記得的工作，回報「忙碌」：不算失敗，這台車先休息，任務交給別台。
        await CreateLotAsync();
        var first = await DispatchAsync(Online("agv-a"), Online("agv-b"));
        await ApplyAsync(first, VehicleEventType.Rejected, VehicleRejectReasons.Busy);

        var second = await DispatchAsync(Online("agv-a"), Online("agv-b"));
        Assert.NotEqual(first.VehicleId, second.VehicleId);
        await ApplyAsync(second, VehicleEventType.Rejected, VehicleRejectReasons.Busy);

        // 兩台都在休息：這一輪派不出去，任務還在等，不會失敗。
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(0, await scope.Transport.DispatchPendingAsync([Online("agv-a"), Online("agv-b")], default));
            Assert.Equal(TransportTaskStatus.Pending, (await scope.Db.TransportTasks.SingleAsync()).Status);
        }

        // 忙碌回報再多次也不會讓任務失敗。
        for (var i = 0; i < _database.Fleet.MaxDispatchAttempts + 1; i++)
        {
            _database.Time.Advance(TransportService.BusyCooldown + TimeSpan.FromSeconds(1));
            await ApplyAsync(await DispatchAsync(Online("agv-a")), VehicleEventType.Rejected, VehicleRejectReasons.Busy);
        }

        await using (var scope = _database.NewScope())
        {
            Assert.Equal(TransportTaskStatus.Pending, (await scope.Db.TransportTasks.SingleAsync()).Status);
        }

        // 真正的拒絕還是照次數算：再被拒絕 3 次才失敗。
        for (var i = 1; i <= _database.Fleet.MaxDispatchAttempts; i++)
        {
            _database.Time.Advance(TransportService.BusyCooldown + TimeSpan.FromSeconds(1));
            await ApplyAsync(await DispatchAsync(Online("agv-a")), VehicleEventType.Rejected, "Blocked");
            await using var scope = _database.NewScope();
            var expected = i < _database.Fleet.MaxDispatchAttempts ? TransportTaskStatus.Pending : TransportTaskStatus.Failed;
            Assert.Equal(expected, (await scope.Db.TransportTasks.SingleAsync()).Status);
        }
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
    public async Task Machine_counts_piece_by_piece_and_releases_the_lot_only_when_all_pieces_are_done()
    {
        var lot = await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        Assert.Equal((TransportTaskStatus.Completed, LotStatus.Processing, "rough-1"), await StateAsync(lot.Id));

        // 每件 2 秒：25 秒後做完 12 件，備料區剩 18 件、成品區 12 件，批次還不能搬走。
        _database.Time.Advance(TimeSpan.FromSeconds(25));
        await AdvanceOperationsAsync();
        var rough = await StationAsync("rough-1");
        Assert.Equal((18, 12, 12, 30), (rough.InboundQuantity, rough.OutboundQuantity, rough.Machine().Machining!.PiecesCompleted, rough.Machine().Machining!.Quantity));
        Assert.Equal((12, 500, false), (rough.Machine().Tool!.PiecesUsed, rough.Machine().Tool!.LifePieces, rough.Machine().Tool!.Changing));
        Assert.Equal(LotStatus.Processing, (await StateAsync(lot.Id)).Lot);

        // 第 60 秒做完最後一件：整批放行，建立下一趟搬運。
        _database.Time.Advance(TimeSpan.FromSeconds(34));
        await AdvanceOperationsAsync();
        Assert.Equal(LotStatus.Processing, (await StateAsync(lot.Id)).Lot);
        _database.Time.Advance(TimeSpan.FromSeconds(1));
        await AdvanceOperationsAsync();

        await using var scope = _database.NewScope();
        var stored = await scope.Db.Lots.SingleAsync();
        var next = await scope.Db.TransportTasks.SingleAsync(t => t.Status == TransportTaskStatus.Pending);
        Assert.Equal((LotStatus.AwaitingTransport, "rough-1", 2), (stored.Status, stored.StationCode, stored.CurrentStepSequence!.Value));
        Assert.Equal(("rough-1", "fine", TransportTaskKind.WipTransfer), (next.FromStationCode, TaskTarget.Of(next), next.Kind));

        rough = await StationAsync("rough-1");
        Assert.Equal((0, 30, null), (rough.InboundQuantity, rough.OutboundQuantity, rough.Machine().Machining));
    }

    [Fact]
    public async Task Machine_pauses_for_a_tool_change_when_the_tool_reaches_its_life()
    {
        // 刀具壽命 10 件、換刀 20 秒：30 件的批次會在第 10、20 件之後各停 20 秒，做完第 30 件時再換一次。
        await ConfigureAsync(s => s.Steps[1].ToolLifePieces = 10);
        var lot = await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        var startedAt = _database.Time.GetUtcNow();

        _database.Time.Advance(TimeSpan.FromSeconds(30));
        await AdvanceOperationsAsync();
        var rough = await StationAsync("rough-1");
        Assert.Equal(10, rough.Machine().Machining!.PiecesCompleted);
        Assert.Equal((true, 0.5), (rough.Machine().Tool!.Changing, rough.Machine().Tool!.ChangeProgress));

        // 換刀結束後從頭計時下一件：第 40 秒換好，第 42 秒做完第 11 件，計數從 1 開始。
        _database.Time.Advance(TimeSpan.FromSeconds(12));
        await AdvanceOperationsAsync();
        rough = await StationAsync("rough-1");
        Assert.Equal((11, false, 1), (rough.Machine().Machining!.PiecesCompleted, rough.Machine().Tool!.Changing, rough.Machine().Tool!.PiecesUsed));

        // 加工 60 秒＋兩次換刀 40 秒＝第 100 秒完成。
        _database.Time.Advance(TimeSpan.FromSeconds(57));
        await AdvanceOperationsAsync();
        Assert.Equal(LotStatus.Processing, (await StateAsync(lot.Id)).Lot);
        _database.Time.Advance(TimeSpan.FromSeconds(1));
        await AdvanceOperationsAsync();
        Assert.Equal(LotStatus.AwaitingTransport, (await StateAsync(lot.Id)).Lot);

        await using (var scope = _database.NewScope())
        {
            Assert.Equal(startedAt.AddSeconds(100), (await scope.Db.Operations.SingleAsync()).CompletedAt);
            Assert.Equal(2, await scope.Db.ToolChanges.CountAsync());
            Assert.True((await scope.Db.MachineTools.SingleAsync(t => t.MachineCode == "rough-1")).IsChanging);
        }

        // 機台閒置時換刀照樣會結束，並留下第三筆紀錄。
        _database.Time.Advance(TimeSpan.FromSeconds(20));
        await AdvanceOperationsAsync();
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(new[] { 10, 10, 10 }, await scope.Db.ToolChanges.OrderBy(c => c.Id).Select(c => c.PiecesUsed).ToListAsync());
            Assert.Equal((0, false), ((await StationAsync("rough-1")).Machine().Tool!.PiecesUsed, (await StationAsync("rough-1")).Machine().Tool!.Changing));
        }
    }

    [Fact]
    public async Task Catching_up_after_downtime_gives_the_same_result_as_ticking_continuously()
    {
        // 後端停機一段時間後一次補算：每一件與每次換刀的時間點都由資料庫裡的時間推算，結果和持續執行相同。
        await ConfigureAsync(s => s.Steps[1].ToolLifePieces = 10);
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        var startedAt = _database.Time.GetUtcNow();

        _database.Time.Advance(TimeSpan.FromMinutes(10));
        await AdvanceOperationsAsync();

        await using var scope = _database.NewScope();
        var operation = await scope.Db.Operations.SingleAsync();
        Assert.Equal((OperationStatus.Completed, 30, startedAt.AddSeconds(100)), (operation.Status, operation.PiecesCompleted, operation.CompletedAt));
        Assert.Equal(3, await scope.Db.ToolChanges.CountAsync());
        Assert.Equal(startedAt.AddSeconds(120), (await scope.Db.ToolChanges.OrderBy(c => c.Id).LastAsync()).CompletedAt);
    }

    [Fact]
    public async Task Test_mode_speeds_up_machining_and_tool_changes_at_the_station_by_its_own_factor()
    {
        await ConfigureAsync(s => s.Steps[1].ToolLifePieces = 10);
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();

        // 測試模式關閉 5 秒：每件 2 秒，做完 2 件，第 3 件做到一半。
        _database.Time.Advance(TimeSpan.FromSeconds(5));
        await AdvanceOperationsAsync();
        Assert.Equal(2, (await StationAsync("rough-1")).Machine().Machining!.PiecesCompleted);

        // 專用倍率設好但測試模式沒開：沒有作用。
        await ConfigureAsync(s => s.Steps[1].TestSpeed = 10);
        await AdvanceOperationsAsync();
        Assert.Equal(2, (await StationAsync("rough-1")).Machine().Machining!.PiecesCompleted);

        // 開啟測試模式（粗加工 10 倍）：進行中的那一件從切換的時間重新計時（不會把之前的 5 秒也用 10 倍計算），之後每件 0.2 秒。
        await ConfigureAsync(s => s.TestMode = true);
        _database.Time.Advance(TimeSpan.FromSeconds(1));
        await AdvanceOperationsAsync();
        Assert.Equal(7, (await StationAsync("rough-1")).Machine().Machining!.PiecesCompleted);

        // 再 0.6 秒做到第 10 件，開始換刀；換刀 20 秒在這一站的 10 倍下是 2 秒。
        _database.Time.Advance(TimeSpan.FromSeconds(1.6));
        await AdvanceOperationsAsync();
        var rough = await StationAsync("rough-1");
        Assert.Equal((10, true, 0.5), (rough.Machine().Machining!.PiecesCompleted, rough.Machine().Tool!.Changing, rough.Machine().Tool!.ChangeProgress));

        _database.Time.Advance(TimeSpan.FromSeconds(1.2));
        await AdvanceOperationsAsync();
        rough = await StationAsync("rough-1");
        Assert.Equal((11, false), (rough.Machine().Machining!.PiecesCompleted, rough.Machine().Tool!.Changing));
    }

    [Fact]
    public async Task Each_lathe_waits_until_its_own_finished_lot_is_picked_up()
    {
        // 粗加工兩台車床：第一批 10 件在 1 號機、第二批 15 件在 2 號機；第三批送到時兩台都在忙，派車時挑路程較近的 1 號機，排在它的備料區。
        await ConfigureAsync(s => s.LotQuantity = 10);
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        await ConfigureAsync(s => s.LotQuantity = 15);
        await CreateLotAsync();
        await DeliverAsync("agv-b");
        await AdvanceOperationsAsync();
        await CreateLotAsync();
        await DeliverAsync("agv-c");
        await AdvanceOperationsAsync();
        var rough1 = await StationAsync("rough-1");
        var rough2 = await StationAsync("rough-2");
        Assert.Equal((10, 15), (rough1.Machine().Machining!.Quantity, rough2.Machine().Machining!.Quantity));
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(1, await scope.Db.Operations.CountAsync(o => o.Status == OperationStatus.Queued));
            Assert.Equal("rough-1", await scope.Db.Operations.Where(o => o.Status == OperationStatus.Queued).Select(o => o.StationCode).SingleAsync());
        }

        // 第 20 秒第一批做完、還沒被搬走：1 號機不接第三批（機邊的成品台只放一框）；2 號機還在做第二批。
        _database.Time.Advance(TimeSpan.FromSeconds(21));
        await AdvanceOperationsAsync();
        rough1 = await StationAsync("rough-1");
        rough2 = await StationAsync("rough-2");
        Assert.Null(rough1.Machine().Machining);
        Assert.Equal(15, rough2.Machine().Machining!.Quantity);

        // 第一批取走後，1 號機接第三批。
        var pickup = await DispatchAsync(Online("agv-a"));
        await ApplyAsync(pickup, VehicleEventType.Accepted);
        await ApplyAsync(pickup, VehicleEventType.Loaded);
        await AdvanceOperationsAsync();
        rough1 = await StationAsync("rough-1");
        rough2 = await StationAsync("rough-2");
        Assert.Equal((15, 15), (rough1.Machine().Machining!.Quantity, rough2.Machine().Machining!.Quantity));

        // 第二批做完、沒被搬走：只卡住 2 號機，1 號機照常做。
        _database.Time.Advance(TimeSpan.FromSeconds(10));
        await AdvanceOperationsAsync();
        rough1 = await StationAsync("rough-1");
        rough2 = await StationAsync("rough-2");
        Assert.Null(rough2.Machine().Machining);
        Assert.NotNull(rough1.Machine().Machining);
    }

    [Fact]
    public async Task Pieces_still_waiting_at_the_machine_count_against_the_inbound_capacity()
    {
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        await CreateLotAsync();
        await DeliverAsync("agv-b");
        await AdvanceOperationsAsync();
        await CreateLotAsync();

        // 兩台都加工到一半：備料區還有 20＋20 件，再送 30 件會超過兩台的容量 60，所以任務等待。
        _database.Time.Advance(TimeSpan.FromSeconds(20));
        await AdvanceOperationsAsync();
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(0, await scope.Db.TransportTasks.CountAsync(t => t.Status == TransportTaskStatus.Assigned));
            Assert.Equal(0, await scope.Transport.DispatchPendingAsync([Online("agv-c")], default));
        }

        // 兩台都做完後備料區空了，第三批可以送來；兩台做完的框也要搬走，所以三台車各接一趟。
        _database.Time.Advance(TimeSpan.FromSeconds(40));
        await AdvanceOperationsAsync();
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(3, await scope.Transport.DispatchPendingAsync([Online("agv-a"), Online("agv-b"), Online("agv-c")], default));
            var routes = await scope.Db.TransportTasks
                .Where(t => t.Status == TransportTaskStatus.Assigned)
                .Select(t => t.FromStationCode + ">" + t.ToStationCode)
                .ToListAsync();
            // 兩框各 30 件，精加工每站的備料區只放得下一框，所以兩框分到 1、2 號機各一台（放不下的那一框送到另一站）。
            // 兩框幾乎同時做完：先派的那框去路程較近的精加工 1 號機，另一框最近的也是 1 號機，但 1 號機已經滿了，所以送到 2 號機。
            // 哪一框先派車取決於完成的先後，兩種順序都合規則，所以只檢查「兩框各去一台精加工」。
            Assert.Equal("raw-store>rough-1", routes.Single(r => r.StartsWith("raw-store>")));
            var finished = routes.Where(r => !r.StartsWith("raw-store>")).ToList();
            Assert.Equal(new[] { "rough-1", "rough-2" }, finished.Select(r => r.Split('>')[0]).Order());
            Assert.Equal(new[] { "fine-1", "fine-2" }, finished.Select(r => r.Split('>')[1]).Order());
        }
    }

    [Fact]
    public async Task Transport_command_carries_load_and_handover_time_from_the_settings()
    {
        await ConfigureAsync(s => s.LotQuantity = 12);
        await CreateLotAsync();
        await DispatchAsync(Online("agv-a"));

        await using var scope = _database.NewScope();
        var payload = (await scope.Db.OutboxMessages.SingleAsync()).Payload;

        // AGV 是一框一框交接：取料、卸料各 30 秒，和件數無關。
        Assert.Contains("\"loadQuantity\":12", payload);
        Assert.Contains("\"loadCode\":\"upper-punch\"", payload);
        Assert.Contains("\"handoverSeconds\":30", payload);
    }

    [Fact]
    public async Task Test_mode_shortens_the_handover_time_in_new_transport_commands()
    {
        // 測試模式下交接依設定的倍率加快（示範值 3000 倍）：30 秒變 0.01 秒。派出的那一刻決定。
        await ConfigureAsync(s => s.TestMode = true);
        await CreateLotAsync();
        await DispatchAsync(Online("agv-a"));

        await using var scope = _database.NewScope();
        Assert.Contains("\"handoverSeconds\":0.01", (await scope.Db.OutboxMessages.SingleAsync()).Payload);
    }

    [Fact]
    public async Task Last_step_sends_the_lot_to_the_buffer_where_it_is_staged()
    {
        var lot = await CreateLotAsync();

        foreach (var station in new[] { "rough-1", "fine-1" })
        {
            await DeliverAsync("agv-a");
            await AdvanceOperationsAsync();
            _database.Time.Advance(TimeSpan.FromSeconds(60));
            await AdvanceOperationsAsync();
            Assert.Equal((LotStatus.AwaitingTransport, station), ((await StateAsync(lot.Id)).Lot, (await StateAsync(lot.Id)).Location));
        }

        await DeliverAsync("agv-a");

        Assert.Equal((TransportTaskStatus.Completed, LotStatus.Staged, "heat-1"), await StateAsync(lot.Id));
        await using var scope = _database.NewScope();
        Assert.Equal(7, await scope.Db.InventoryMovements.CountAsync());
        Assert.Equal(2, await scope.Db.Operations.CountAsync(o => o.Status == OperationStatus.Completed));

        // 兩站的刀具各累計 30 件；暫存點顯示已暫存的件數。
        Assert.Equal(30, (await StationAsync("rough-1")).Machine().Tool!.PiecesUsed);
        Assert.Equal(30, (await StationAsync("fine-1")).Machine().Tool!.PiecesUsed);
        Assert.Equal((30, false), ((await StationAsync("heat-1")).InboundQuantity, (await StationAsync("heat-1")).IsMachine));
    }

    [Fact]
    public async Task Settings_can_be_changed_and_are_validated()
    {
        // 示範預設值要看沒有被測試改過的資料庫。
        await using (var seeded = (await TestDatabase.CreateAsync(postgres)).NewScope())
        {
            var defaults = await seeded.Production.GetSettingsAsync(default);
            Assert.Equal(new[] { "cutting", "rough", "fine", "heat", "temper", "plating", "polish", "qc", "packing" }, defaults.Steps.Select(step => step.Code));
            // 切料每支 5 分鐘（使用者 2026-10-22）。
            Assert.Equal((300d, true), (defaults.Steps[0].SecondsPerPiece, defaults.Steps[0].IsCutting));
            // 逐件工序：粗加工、精加工、拋光，以及品管（逐支檢驗 60 秒）、包裝（逐支 60 秒）。
            Assert.Equal(new[] { (30d, 500, false, false), (60d, 250, false, false), (20d, 0, false, false), (60d, 0, true, false), (60d, 0, false, true) },
                defaults.Steps.Where(step => step.Kind == ProcessStepKind.Piece && !step.IsCutting)
                    .Select(step => (step.SecondsPerPiece, step.ToolLifePieces, step.IsInspection, step.IsPacking)));
            // 淬火 4 小時、回火 8 小時，各 150～300 支；電鍍 1 小時，有料就開，一爐 30 mm 40 支／24 mm 56 支。
            Assert.Equal(new[] { (150, 300, 0, 14400d), (150, 300, 0, 28800d), (1, 40, 56, 3600d) },
                defaults.Steps.Where(step => step.Kind == ProcessStepKind.Batch)
                    .Select(step => (step.BatchMinPieces, step.BatchMaxPieces, step.BatchMaxPiecesRod24, step.BatchSeconds)));
            // 包好的出貨箱送到出貨站；貨車每小時一班，測試模式和各站一樣 3000 倍。
            Assert.Equal(("shipping", 3600d, 3000d), (defaults.BufferStationCode, defaults.TruckIntervalSeconds, defaults.TestTruckSpeed));
            // 品管：不良率 10%，同一支最多反工 1 次。
            Assert.Equal((0.1, 1), (defaults.DefectRate, defaults.MaxReworkCount));
            // 測試模式預設關閉；各站的專用倍率與 AGV 交接都是 3000（使用者 2026-10-22：一個流程約 2～3 分鐘）；空車與載貨都 5 倍。
            Assert.False(defaults.TestMode);
            Assert.All(defaults.Steps, step => Assert.Equal(3000d, step.TestSpeed));
            Assert.Equal((600d, 3000d, 5d, 5d),
                (defaults.ChangeoverSeconds, defaults.TestHandoverSpeed, defaults.TestEmptyVehicleSpeed, defaults.TestLoadedVehicleSpeed));
            Assert.Equal((5d, 30d, 90d, 50, true),
                (defaults.PieceHandlingSeconds, defaults.AgvHandoverSeconds, defaults.ToolChangeSeconds, defaults.ToolRequestThresholdPieces, defaults.ToolDeliveryEnabled));
        }

        await using var scope = _database.NewScope();
        var settings = await scope.Production.GetSettingsAsync(default);
        Assert.Equal((30, 300, 0d, 20d), (settings.LotQuantity, settings.MaxLotQuantity, settings.PieceHandlingSeconds, settings.ToolChangeSeconds));

        settings.LotQuantity = 12;
        settings.PieceHandlingSeconds = 1.5;
        settings.AgvHandoverSeconds = 12;
        settings.ToolChangeSeconds = 45;
        settings.Steps[1].SecondsPerPiece = 4;
        settings.Steps[1].ToolLifePieces = 100;
        var updated = await scope.Production.UpdateSettingsAsync(settings, default);
        Assert.Equal((12, 1.5, 12d, 45d), (updated.LotQuantity, updated.PieceHandlingSeconds, updated.AgvHandoverSeconds, updated.ToolChangeSeconds));
        Assert.Equal(new[] { (2d, 0), (4d, 100), (2d, 250) }, updated.Steps.Select(step => (step.SecondsPerPiece, step.ToolLifePieces)));
        Assert.Equal(12, ((await scope.Production.CreateBatchAsync(null, null, [ProductParts.UpperPunch], default)).Single()).Quantity);

        // 一個批次最多 300 支。
        await Invalid(s => s.LotQuantity = 301);
        await Invalid(s => s.Steps[2].SecondsPerPiece = 0);
        await Invalid(s => s.Steps[2].TestSpeed = 0.5);
        await Invalid(s => s.Steps[2].TestSpeed = 5001);
        await Invalid(s => s.ChangeoverSeconds = -1);
        await Invalid(s => s.TestHandoverSpeed = 0);
        await Invalid(s => s.TestEmptyVehicleSpeed = 6);
        await Invalid(s => s.DefectRate = 1.5);
        await Invalid(s => s.MaxReworkCount = 4);
        await Invalid(s => s.Steps[2].ToolLifePieces = 0);
        await Invalid(s => s.PieceHandlingSeconds = -1);
        await Invalid(s => s.AgvHandoverSeconds = -1);
        await Invalid(s => s.ToolRequestThresholdPieces = -1);
        await Invalid(s => s.ToolChangeSeconds = double.NaN);
        await Assert.ThrowsAsync<RequestValidationException>(() => scope.Production.CreateBatchAsync(0, null, [ProductParts.UpperPunch], default));

        async Task Invalid(Action<ProductionSettingsDto> change)
        {
            var request = await scope.Production.GetSettingsAsync(default);
            change(request);
            await Assert.ThrowsAsync<RequestValidationException>(() => scope.Production.UpdateSettingsAsync(request, default));
        }
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
        var lot = (await scope.Production.CreateBatchAsync(null, null, [ProductParts.UpperPunch], default)).Single();
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

    private async Task ConfigureAsync(Action<ProductionSettingsDto> change)
    {
        await using var scope = _database.NewScope();
        var settings = await scope.Production.GetSettingsAsync(default);
        change(settings);
        await scope.Production.UpdateSettingsAsync(settings, default);
    }

    private async Task<StationStatusDto> StationAsync(string code)
    {
        await using var scope = _database.NewScope();
        return (await scope.Production.GetStationStatusAsync(default)).Single(s => s.Code == code);
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
