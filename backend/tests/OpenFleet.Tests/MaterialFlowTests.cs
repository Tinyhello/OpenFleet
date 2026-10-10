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
    public async Task Vehicle_with_the_shortest_drive_along_the_road_network_is_chosen()
    {
        await CreateLotAsync();

        // 取料點（原料倉）在 (-16, -7.6)。agv-a 的直線距離比較近，但它在充電車位裡，要先繞出來；
        // agv-b 在外圈車道上，沿路網開過去比較短。
        var assignment = await DispatchAsync(Online("agv-a", x: -10.3, z: 0.2), Online("agv-b", x: -8, z: -6));

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
        Assert.Equal((TransportTaskKind.WipTransfer, "rough", "fine"), (assigned.Kind, assigned.FromStationCode, assigned.ToStationCode));
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
    public async Task Machine_counts_piece_by_piece_and_releases_the_lot_only_when_all_pieces_are_done()
    {
        var lot = await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        Assert.Equal((TransportTaskStatus.Completed, LotStatus.Processing, "rough"), await StateAsync(lot.Id));

        // 每件 2 秒：25 秒後做完 12 件，備料區剩 18 件、成品區 12 件，批次還不能搬走。
        _database.Time.Advance(TimeSpan.FromSeconds(25));
        await AdvanceOperationsAsync();
        var rough = await StationAsync("rough");
        Assert.Equal((18, 12, 12, 30), (rough.InboundQuantity, rough.OutboundQuantity, rough.Machining!.PiecesCompleted, rough.Machining.Quantity));
        Assert.Equal((12, 500, false), (rough.Tool!.PiecesUsed, rough.Tool.LifePieces, rough.Tool.Changing));
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
        Assert.Equal((LotStatus.AwaitingTransport, "rough", 2), (stored.Status, stored.StationCode, stored.CurrentStepSequence!.Value));
        Assert.Equal(("rough", "fine", TransportTaskKind.WipTransfer), (next.FromStationCode, next.ToStationCode, next.Kind));

        rough = await StationAsync("rough");
        Assert.Equal((0, 30, null), (rough.InboundQuantity, rough.OutboundQuantity, rough.Machining));
    }

    [Fact]
    public async Task Machine_pauses_for_a_tool_change_when_the_tool_reaches_its_life()
    {
        // 刀具壽命 10 件、換刀 20 秒：30 件的批次會在第 10、20 件之後各停 20 秒，做完第 30 件時再換一次。
        await ConfigureAsync(s => s.Steps[0].ToolLifePieces = 10);
        var lot = await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        var startedAt = _database.Time.GetUtcNow();

        _database.Time.Advance(TimeSpan.FromSeconds(30));
        await AdvanceOperationsAsync();
        var rough = await StationAsync("rough");
        Assert.Equal(10, rough.Machining!.PiecesCompleted);
        Assert.Equal((true, 0.5), (rough.Tool!.Changing, rough.Tool.ChangeProgress));

        // 換刀結束後從頭計時下一件：第 40 秒換好，第 42 秒做完第 11 件，計數從 1 開始。
        _database.Time.Advance(TimeSpan.FromSeconds(12));
        await AdvanceOperationsAsync();
        rough = await StationAsync("rough");
        Assert.Equal((11, false, 1), (rough.Machining!.PiecesCompleted, rough.Tool!.Changing, rough.Tool.PiecesUsed));

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
            Assert.True((await scope.Db.StationTools.SingleAsync(t => t.StationCode == "rough")).IsChanging);
        }

        // 機台閒置時換刀照樣會結束，並留下第三筆紀錄。
        _database.Time.Advance(TimeSpan.FromSeconds(20));
        await AdvanceOperationsAsync();
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(new[] { 10, 10, 10 }, await scope.Db.ToolChanges.OrderBy(c => c.Id).Select(c => c.PiecesUsed).ToListAsync());
            Assert.Equal((0, false), ((await StationAsync("rough")).Tool!.PiecesUsed, (await StationAsync("rough")).Tool!.Changing));
        }
    }

    [Fact]
    public async Task Catching_up_after_downtime_gives_the_same_result_as_ticking_continuously()
    {
        // 後端停機一段時間後一次補算：每一件與每次換刀的時間點都由資料庫裡的時間推算，結果和持續執行相同。
        await ConfigureAsync(s => s.Steps[0].ToolLifePieces = 10);
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
    public async Task Time_scale_speeds_up_machining_and_tool_changes()
    {
        await ConfigureAsync(s => s.Steps[0].ToolLifePieces = 10);
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();

        // 1 倍速 5 秒：每件 2 秒，做完 2 件，第 3 件做到一半。
        _database.Time.Advance(TimeSpan.FromSeconds(5));
        await AdvanceOperationsAsync();
        Assert.Equal(2, (await StationAsync("rough")).Machining!.PiecesCompleted);

        // 切到 10 倍：進行中的那一件從切換的時間重新計時（不會把之前的 5 秒也用 10 倍計算），之後每件 0.2 秒。
        _database.Speed.Set(10);
        _database.Time.Advance(TimeSpan.FromSeconds(1));
        await AdvanceOperationsAsync();
        Assert.Equal(7, (await StationAsync("rough")).Machining!.PiecesCompleted);

        // 再 0.6 秒做到第 10 件，開始換刀；換刀 20 秒在 10 倍速下是 2 秒。
        _database.Time.Advance(TimeSpan.FromSeconds(1.6));
        await AdvanceOperationsAsync();
        var rough = await StationAsync("rough");
        Assert.Equal((10, true, 0.5), (rough.Machining!.PiecesCompleted, rough.Tool!.Changing, rough.Tool.ChangeProgress));

        _database.Time.Advance(TimeSpan.FromSeconds(1.2));
        await AdvanceOperationsAsync();
        rough = await StationAsync("rough");
        Assert.Equal((11, false), (rough.Machining!.PiecesCompleted, rough.Tool!.Changing));
    }

    [Fact]
    public async Task Machine_does_not_start_the_next_lot_until_the_finished_one_is_picked_up()
    {
        // 兩個 15 件的批次：第一批加工中時，備料區還放得下第二批。
        await ConfigureAsync(s => s.LotQuantity = 15);
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();

        await CreateLotAsync();
        await DeliverAsync("agv-b");
        await AdvanceOperationsAsync();
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(1, await scope.Db.Operations.CountAsync(o => o.Status == OperationStatus.Running));
            Assert.Equal(1, await scope.Db.Operations.CountAsync(o => o.Status == OperationStatus.Queued));
        }

        // 第一批完成但還沒被搬走：第二批不能開始。
        _database.Time.Advance(TimeSpan.FromSeconds(30));
        await AdvanceOperationsAsync();
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(0, await scope.Db.Operations.CountAsync(o => o.Status == OperationStatus.Running));
        }

        var rough = await StationAsync("rough");
        Assert.Equal((15, 15), (rough.InboundQuantity, rough.OutboundQuantity));

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
    public async Task Pieces_still_waiting_at_the_machine_count_against_the_inbound_capacity()
    {
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        await CreateLotAsync();

        // 加工到一半：備料區還有 20 件，再送 30 件會超過容量 30，所以任務等待。
        _database.Time.Advance(TimeSpan.FromSeconds(20));
        await AdvanceOperationsAsync();
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(0, await scope.Db.TransportTasks.CountAsync(t => t.Status == TransportTaskStatus.Assigned));
            Assert.Equal(0, await scope.Transport.DispatchPendingAsync([Online("agv-b")], default));
        }

        // 整批做完後備料區空了，第二批可以送來；成品區的第一批也要搬走，所以兩台車各接一趟。
        _database.Time.Advance(TimeSpan.FromSeconds(40));
        await AdvanceOperationsAsync();
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(2, await scope.Transport.DispatchPendingAsync([Online("agv-a"), Online("agv-b")], default));
            var routes = await scope.Db.TransportTasks
                .Where(t => t.Status == TransportTaskStatus.Assigned)
                .Select(t => t.FromStationCode + ">" + t.ToStationCode)
                .ToListAsync();
            Assert.Equal(new[] { "raw-store>rough", "rough>fine" }, routes.Order());
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
    public async Task Last_step_sends_the_lot_to_the_buffer_where_it_is_staged()
    {
        var lot = await CreateLotAsync();

        foreach (var station in new[] { "rough", "fine" })
        {
            await DeliverAsync("agv-a");
            await AdvanceOperationsAsync();
            _database.Time.Advance(TimeSpan.FromSeconds(60));
            await AdvanceOperationsAsync();
            Assert.Equal((LotStatus.AwaitingTransport, station), ((await StateAsync(lot.Id)).Lot, (await StateAsync(lot.Id)).Location));
        }

        await DeliverAsync("agv-a");

        Assert.Equal((TransportTaskStatus.Completed, LotStatus.Staged, "heat"), await StateAsync(lot.Id));
        await using var scope = _database.NewScope();
        Assert.Equal(7, await scope.Db.InventoryMovements.CountAsync());
        Assert.Equal(2, await scope.Db.Operations.CountAsync(o => o.Status == OperationStatus.Completed));

        // 兩站的刀具各累計 30 件；暫存點顯示已暫存的件數。
        Assert.Equal(30, (await StationAsync("rough")).Tool!.PiecesUsed);
        Assert.Equal(30, (await StationAsync("fine")).Tool!.PiecesUsed);
        Assert.Equal((30, false), ((await StationAsync("heat")).InboundQuantity, (await StationAsync("heat")).IsMachine));
    }

    [Fact]
    public async Task Settings_can_be_changed_and_are_validated()
    {
        // 示範預設值要看沒有被測試改過的資料庫。
        await using (var seeded = (await TestDatabase.CreateAsync(postgres)).NewScope())
        {
            var defaults = await seeded.Production.GetSettingsAsync(default);
            Assert.Equal(new[] { (30d, 500), (60d, 250) }, defaults.Steps.Select(step => (step.SecondsPerPiece, step.ToolLifePieces)));
            Assert.Equal((5d, 30d, 90d, 50, true),
                (defaults.PieceHandlingSeconds, defaults.AgvHandoverSeconds, defaults.ToolChangeSeconds, defaults.ToolRequestThresholdPieces, defaults.ToolDeliveryEnabled));
        }

        await using var scope = _database.NewScope();
        var settings = await scope.Production.GetSettingsAsync(default);
        Assert.Equal((30, 30, 0d, 20d), (settings.LotQuantity, settings.MaxLotQuantity, settings.PieceHandlingSeconds, settings.ToolChangeSeconds));

        settings.LotQuantity = 12;
        settings.PieceHandlingSeconds = 1.5;
        settings.AgvHandoverSeconds = 12;
        settings.ToolChangeSeconds = 45;
        settings.Steps[0].SecondsPerPiece = 4;
        settings.Steps[0].ToolLifePieces = 100;
        var updated = await scope.Production.UpdateSettingsAsync(settings, default);
        Assert.Equal((12, 1.5, 12d, 45d), (updated.LotQuantity, updated.PieceHandlingSeconds, updated.AgvHandoverSeconds, updated.ToolChangeSeconds));
        Assert.Equal(new[] { (4d, 100), (2d, 250) }, updated.Steps.Select(step => (step.SecondsPerPiece, step.ToolLifePieces)));
        Assert.Equal(12, (await scope.Production.CreateLotAsync(null, default)).Quantity);

        // 待加工料容量是 30：不拆批時批量不能超過它。
        await Invalid(s => s.LotQuantity = 31);
        await Invalid(s => s.Steps[1].SecondsPerPiece = 0);
        await Invalid(s => s.Steps[1].ToolLifePieces = 0);
        await Invalid(s => s.PieceHandlingSeconds = -1);
        await Invalid(s => s.AgvHandoverSeconds = -1);
        await Invalid(s => s.ToolRequestThresholdPieces = -1);
        await Invalid(s => s.ToolChangeSeconds = double.NaN);
        await Assert.ThrowsAsync<RequestValidationException>(() => scope.Production.CreateLotAsync(0, default));

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
