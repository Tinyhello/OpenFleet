using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Contracts;
using static OpenFleet.Tests.TestDatabase;

namespace OpenFleet.Tests;

/// <summary>
/// P3 第二步：缺車等待區間與機台耽誤時間；車都在忙（缺車）與全部離線（重大事故）分開記錄。
/// 時間由 FakeTimeProvider 控制，每件加工 2 秒。大型車 agv-c 從頭到尾都在忙，用來製造「有車在線但叫不到」。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VehicleShortageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly VehicleState Busy = Online("agv-c");

    private TestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(postgres);
        await _database.UseSimpleMachiningAsync();
        await _database.OccupyAsync("agv-c");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Wait_runs_from_all_vehicles_busy_until_dispatch_and_is_measured_in_simulated_time()
    {
        await CreateLotAsync();

        // 唯一在線的車在忙：任務開始等待；粗加工沒有東西可做，開始算耽誤。
        Assert.Equal(0, await DispatchAsync(Busy));
        _database.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await DispatchAsync(Busy));

        var summary = await SummaryAsync(Busy);
        Assert.Equal((1, 1, 10d), (summary.WaitingTasks, summary.WaitCount, summary.WaitSeconds));
        Assert.Equal((10d, MachineDelayCause.WaitingForMaterial, ShortageReason.VehiclesBusy),
            (summary.Steps[1].DelaySeconds, summary.Steps[1].CurrentCause, summary.Steps[1].CurrentReason));
        Assert.Equal((0d, null), (summary.Steps[2].DelaySeconds, summary.Steps[2].CurrentCause));
        Assert.Equal((0, 0d), (summary.OfflineWaitCount, summary.Steps[1].OfflineSeconds));

        // 再等 2 秒；派出車的那一刻等待與耽誤都結束，之後的行駛時間不算。
        _database.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await DispatchAsync(Busy, Online("agv-a")));
        _database.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await DispatchAsync(Busy, Online("agv-a")));

        summary = await SummaryAsync(Busy);
        Assert.Equal((0, 1, 12d), (summary.WaitingTasks, summary.WaitCount, summary.WaitSeconds));
        Assert.Equal((12d, 12d, 0d, null), (summary.Steps[1].DelaySeconds, summary.Steps[1].WaitingForMaterialSeconds,
            summary.Steps[1].OutputNotRemovedSeconds, summary.Steps[1].CurrentCause));

        await using var scope = _database.NewScope();
        var wait = await scope.Db.VehicleWaits.SingleAsync();
        Assert.Equal(TimeSpan.FromSeconds(12), wait.EndedAt - wait.StartedAt);
    }

    [Fact]
    public async Task Every_vehicle_offline_is_an_incident_and_is_not_counted_as_shortage()
    {
        await CreateLotAsync();

        // 沒有任何大型車在線：重大事故，不是缺車。
        Assert.Equal(0, await DispatchAsync());
        _database.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await DispatchAsync());

        var summary = await SummaryAsync();
        Assert.Equal(new[] { VehicleType.Large, VehicleType.Small }, summary.OfflineVehicleTypes);
        Assert.Equal((1, 1, 10d), (summary.OfflineWaitingTasks, summary.OfflineWaitCount, summary.OfflineWaitSeconds));
        Assert.Equal((0, 0, 0d), (summary.WaitingTasks, summary.WaitCount, summary.WaitSeconds));
        Assert.Equal((0d, 10d, ShortageReason.FleetOffline), (summary.Steps[1].DelaySeconds, summary.Steps[1].OfflineSeconds, summary.Steps[1].CurrentReason));
        Assert.Equal(ShortageReason.FleetOffline, (await StationAsync("rough")).Machine().ShortageReason);

        // 有一台回到線上但在忙：事故結束，從這一刻起才算缺車。兩段時間各算各的。
        Assert.Equal(0, await DispatchAsync(Busy));
        _database.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await DispatchAsync(Busy));

        summary = await SummaryAsync(Busy, Online("agv-s"));
        Assert.Empty(summary.OfflineVehicleTypes);
        Assert.Equal((0, 1, 10d), (summary.OfflineWaitingTasks, summary.OfflineWaitCount, summary.OfflineWaitSeconds));
        Assert.Equal((1, 1, 5d), (summary.WaitingTasks, summary.WaitCount, summary.WaitSeconds));
        Assert.Equal((5d, 10d, ShortageReason.VehiclesBusy), (summary.Steps[1].DelaySeconds, summary.Steps[1].OfflineSeconds, summary.Steps[1].CurrentReason));
    }

    [Fact]
    public async Task Machine_that_is_still_working_is_not_delayed()
    {
        // 粗加工兩台車床都在加工；第三批叫不到車，但機台沒有停下來，所以只有任務在等，沒有耽誤。
        await ConfigureAsync(s => s.LotQuantity = 15);
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        await CreateLotAsync();
        await DeliverAsync("agv-b");
        await AdvanceOperationsAsync();
        await CreateLotAsync();

        Assert.Equal(0, await DispatchAsync(Busy));
        _database.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await DispatchAsync(Busy));

        var summary = await SummaryAsync(Busy);
        Assert.Equal((1, 10d), (summary.WaitingTasks, summary.WaitSeconds));
        Assert.All(summary.Steps, step => Assert.Equal((0d, null), (step.DelaySeconds, step.CurrentCause)));

        // 換刀中的機台也不算缺車耽誤。
        await using (var scope = _database.NewScope())
        {
            await scope.Db.Operations.ExecuteDeleteAsync();
            foreach (var tool in await scope.Db.MachineTools.Where(t => t.MachineCode.StartsWith("rough-")).ToListAsync())
            {
                tool.StartChange(_database.Time.GetUtcNow(), 60);
            }

            await scope.Db.SaveChangesAsync();
        }

        _database.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await DispatchAsync(Busy));
        Assert.All((await SummaryAsync(Busy)).Steps, step => Assert.Equal(0d, step.DelaySeconds));
    }

    [Fact]
    public async Task Idle_lathe_waits_for_material_while_the_other_lathe_keeps_working()
    {
        // 第一批在粗加工 1 號機；第二批叫不到車：2 號機沒有東西可做，算它等料，1 號機不算。
        await ConfigureAsync(s => s.LotQuantity = 15);
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        await CreateLotAsync();

        Assert.Equal(0, await DispatchAsync(Busy));
        _database.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await DispatchAsync(Busy));

        var summary = await SummaryAsync(Busy);
        Assert.Equal((10d, 10d, MachineDelayCause.WaitingForMaterial), (summary.Steps[1].DelaySeconds, summary.Steps[1].WaitingForMaterialSeconds, summary.Steps[1].CurrentCause));
        var rough = await StationAsync("rough");
        Assert.Equal((null, MachineDelayCause.WaitingForMaterial), (rough.Machine(1).ShortageCause, rough.Machine(2).ShortageCause));
        await using var scope = _database.NewScope();
        Assert.Equal("rough-2", (await scope.Db.MachineDelays.SingleAsync()).MachineCode);
    }

    [Fact]
    public async Task Finished_lot_that_cannot_be_removed_delays_its_machine_and_starves_the_next_one()
    {
        // 三批送到粗加工：第一批 10 件在 1 號機、第二批 15 件在 2 號機，第三批排隊。
        // 兩台都做完（30 秒）卻叫不到車搬走，第三批無法開始。
        await ConfigureAsync(s => s.LotQuantity = 10);
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        await ConfigureAsync(s => s.LotQuantity = 15);
        await CreateLotAsync();
        await DeliverAsync("agv-b");
        await AdvanceOperationsAsync();
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        // 分兩次推進，1 號機的框先做完，搬運任務比較早建立。
        _database.Time.Advance(TimeSpan.FromSeconds(21));
        await AdvanceOperationsAsync();
        _database.Time.Advance(TimeSpan.FromSeconds(10));
        await AdvanceOperationsAsync();

        Assert.Equal(0, await DispatchAsync(Busy));
        _database.Time.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(0, await DispatchAsync(Busy));

        // 粗加工：兩台都被做完的框卡住，但排隊的只有一框，所以只算一台「成品沒被搬走」（先做完的 1 號機）。
        // 精加工：兩框都送不過去，兩台都等不到料，各算一次。
        var summary = await SummaryAsync(Busy);
        Assert.Equal((8d, MachineDelayCause.OutputNotRemoved), (summary.Steps[1].OutputNotRemovedSeconds, summary.Steps[1].CurrentCause));
        Assert.Equal((16d, MachineDelayCause.WaitingForMaterial), (summary.Steps[2].WaitingForMaterialSeconds, summary.Steps[2].CurrentCause));
        var rough = await StationAsync("rough");
        Assert.Equal((MachineDelayCause.OutputNotRemoved, null), (rough.Machine(1).ShortageCause, rough.Machine(2).ShortageCause));

        // 兩台車都派出去之後全部結束。
        Assert.Equal(2, await DispatchAsync(Busy, Online("agv-a"), Online("agv-b")));
        summary = await SummaryAsync(Busy);
        Assert.Equal((0, 8d, 16d), (summary.WaitingTasks, summary.Steps[1].DelaySeconds, summary.Steps[2].DelaySeconds));
        Assert.All(summary.Steps, step => Assert.Null(step.CurrentCause));
        Assert.All((await StationAsync("rough")).Machines, machine => Assert.Null(machine.ShortageCause));
    }

    [Fact]
    public async Task Tasks_waiting_for_the_same_machines_count_each_machine_once()
    {
        // 三批要送粗加工、都叫不到車：任務各等 5 秒，但粗加工只有兩台，耽誤的是 2 台 × 5 秒，不是 3 × 5 秒。
        await ConfigureAsync(s => s.LotQuantity = 15);
        await CreateLotAsync();
        await CreateLotAsync();
        await CreateLotAsync();

        Assert.Equal(0, await DispatchAsync(Busy));
        _database.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await DispatchAsync(Busy));

        var summary = await SummaryAsync(Busy);
        Assert.Equal((3, 15d, 10d), (summary.WaitingTasks, summary.WaitSeconds, summary.Steps[1].DelaySeconds));
    }

    [Fact]
    public async Task Task_that_waits_for_room_at_the_destination_is_not_a_vehicle_shortage()
    {
        // 粗加工兩台的備料區被前兩批占滿：第三批等的是容量，不是車。
        await CreateLotAsync();
        await DeliverAsync("agv-a");
        await AdvanceOperationsAsync();
        await CreateLotAsync();
        await DeliverAsync("agv-b");
        await AdvanceOperationsAsync();
        await CreateLotAsync();

        Assert.Equal(0, await DispatchAsync(Busy));
        _database.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await DispatchAsync(Busy));

        var summary = await SummaryAsync(Busy);
        Assert.Equal((0, 0, 0d), (summary.WaitingTasks, summary.WaitCount, summary.WaitSeconds));
        Assert.All(summary.Steps, step => Assert.Equal(0d, step.DelaySeconds));
    }

    private async Task<MaterialLot> CreateLotAsync()
    {
        await using var scope = _database.NewScope();
        var lot = (await scope.Production.CreateBatchAsync(null, null, [ProductParts.UpperPunch], default)).Single();
        _database.Time.Advance(TimeSpan.FromMilliseconds(10));
        return lot;
    }

    private async Task<int> DispatchAsync(params VehicleState[] online)
    {
        await using var scope = _database.NewScope();
        return await scope.Transport.DispatchPendingAsync(online, default);
    }

    /// <summary>派工並走完接受、取料、卸料；過程中時間不前進。</summary>
    private async Task DeliverAsync(string vehicleId)
    {
        Assert.Equal(1, await DispatchAsync(Busy, Online(vehicleId)));
        await _database.CompleteDeliveryAsync(vehicleId);
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

    private async Task<VehicleShortageDto> SummaryAsync(params VehicleState[] vehicles)
    {
        await using var scope = _database.NewScope();
        return await scope.Shortage.GetSummaryAsync(vehicles, default);
    }

    private async Task<StationStatusDto> StationAsync(string code)
    {
        await using var scope = _database.NewScope();
        return (await scope.Production.GetStationStatusAsync(default)).Single(s => s.Code == code);
    }
}
