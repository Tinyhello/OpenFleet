using Microsoft.EntityFrameworkCore;
using OpenFleet.Contracts;
using static OpenFleet.Tests.TestDatabase;

namespace OpenFleet.Tests;

/// <summary>
/// P3 第三步：小型車送刀具。每件加工 2 秒、刀具壽命 10 件、剩 4 件時叫刀、換刀 20 秒；一批 30 件在粗加工。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ToolDeliveryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(postgres);
        await _database.UseSimpleMachiningAsync();
        await ConfigureAsync(s =>
        {
            s.ToolDeliveryEnabled = true;
            s.ToolRequestThresholdPieces = 4;
            s.Steps[0].ToolLifePieces = 10;
        });

        // 一批送到粗加工並開始加工。
        await using (var scope = _database.NewScope())
        {
            (await scope.Production.CreateBatchAsync(null, null, default)).Single();
        }

        Assert.Equal(1, await DispatchAsync(Online("agv-a"), Online("agv-s")));
        await _database.CompleteDeliveryAsync("agv-a");
        await AdvanceAsync(0);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Spare_tool_is_requested_ahead_and_delivered_by_the_small_vehicle_only()
    {
        // 做完 5 件還剩 5 件：還不用叫刀。
        await AdvanceAsync(10);
        Assert.Equal(ToolSpareStatus.None, (await RoughAsync()).Tool!.Spare);

        // 做完 6 件剩 4 件：建立刀具配送任務（刀具庫 → 粗加工）。
        await AdvanceAsync(2);
        Assert.Equal(ToolSpareStatus.Requested, (await RoughAsync()).Tool!.Spare);
        await using (var scope = _database.NewScope())
        {
            var task = await scope.Db.TransportTasks.SingleAsync(t => t.Kind == TransportTaskKind.ToolDelivery);
            Assert.Equal(("tool-store", "rough", TransportTaskStatus.Pending, null), (task.FromStationCode, task.ToStationCode, task.Status, task.LotId));
        }

        // 同一把刀只叫一次。
        await AdvanceAsync(2);
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(1, await scope.Db.TransportTasks.CountAsync(t => t.Kind == TransportTaskKind.ToolDelivery));
        }

        // 大型車不送刀具；小型車才會被派。
        Assert.Equal(0, await DispatchAsync(Online("agv-a"), Online("agv-b")));
        Assert.Equal(1, await DispatchAsync(Online("agv-a"), Online("agv-s")));
        await using (var scope = _database.NewScope())
        {
            var assignment = await scope.Db.Assignments.SingleAsync(a => a.Status == AssignmentStatus.Dispatched);
            Assert.Equal("agv-s", assignment.VehicleId);
            var command = (await scope.Db.OutboxMessages.OrderByDescending(m => m.Id).FirstAsync()).Payload;
            Assert.Contains("\"loadCode\":\"tool\"", command);
            Assert.Contains("\"loadQuantity\":1", command);
        }

        // 配送完成只代表新刀在機邊：還沒換刀，機台照常加工。
        await _database.CompleteDeliveryAsync("agv-s");
        var rough = await RoughAsync();
        Assert.Equal((ToolSpareStatus.Delivered, false, false), (rough.Tool!.Spare, rough.Tool.Changing, rough.Tool.WaitingForTool));
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(TransportTaskStatus.Completed, (await scope.Db.TransportTasks.SingleAsync(t => t.Kind == TransportTaskKind.ToolDelivery)).Status);
            Assert.Equal(0, await scope.Db.ToolChanges.CountAsync());
        }

        // 第 10 件做完、新刀已在機邊：直接換刀。換完之後備用刀用掉了，換刀紀錄另外一筆。
        await AdvanceAsync(6);
        rough = await RoughAsync();
        Assert.Equal((10, true, false), (rough.Machining!.PiecesCompleted, rough.Tool!.Changing, rough.Tool.WaitingForTool));

        await AdvanceAsync(20);
        rough = await RoughAsync();
        Assert.Equal((false, 0, ToolSpareStatus.None), (rough.Tool!.Changing, rough.Tool.PiecesUsed, rough.Tool.Spare));
        await using (var scope = _database.NewScope())
        {
            Assert.Equal(10, (await scope.Db.ToolChanges.SingleAsync()).PiecesUsed);
        }
    }

    [Fact]
    public async Task Machine_stops_and_waits_when_the_spare_tool_has_not_arrived()
    {
        // 小型車在忙，新刀送不出去：做完第 10 件就停機等刀。
        await _database.OccupyAsync("agv-s");
        await AdvanceAsync(12);
        Assert.Equal(0, await DispatchAsync(Online("agv-a"), Online("agv-s")));
        await AdvanceAsync(8);
        var rough = await RoughAsync();
        Assert.Equal((10, true, false, ToolSpareStatus.Requested),
            (rough.Machining!.PiecesCompleted, rough.Tool!.WaitingForTool, rough.Tool.Changing, rough.Tool.Spare));

        // 停機期間不會繼續加工；這段時間記為缺車造成的「等刀」。
        Assert.Equal(0, await DispatchAsync(Online("agv-a"), Online("agv-s")));
        await AdvanceAsync(6);
        Assert.Equal(0, await DispatchAsync(Online("agv-a"), Online("agv-s")));
        Assert.Equal(10, (await RoughAsync()).Machining!.PiecesCompleted);
        var summary = await SummaryAsync(Online("agv-a"), Online("agv-s"));
        Assert.Equal((6d, 6d, MachineDelayCause.WaitingForTool, ShortageReason.VehiclesBusy),
            (summary.Steps[0].DelaySeconds, summary.Steps[0].WaitingForToolSeconds, summary.Steps[0].CurrentCause, summary.Steps[0].CurrentReason));

        // 小型車有空了：派出的那一刻缺車結束。車還在路上時機台仍然停著，但那是配送時間，不算缺車。
        await _database.ReleaseAsync("agv-s");
        Assert.Equal(1, await DispatchAsync(Online("agv-a"), Online("agv-s")));
        await AdvanceAsync(4);
        Assert.Equal(0, await DispatchAsync(Online("agv-a"), Online("agv-s")));
        summary = await SummaryAsync(Online("agv-a"), Online("agv-s"));
        Assert.Equal((6d, null), (summary.Steps[0].WaitingForToolSeconds, summary.Steps[0].CurrentCause));
        Assert.True((await RoughAsync()).Tool!.WaitingForTool);

        // 新刀送到才開始換刀（20 秒），換完從頭計時下一件。
        await _database.CompleteDeliveryAsync("agv-s");
        await AdvanceAsync(0);
        rough = await RoughAsync();
        Assert.Equal((false, true, 0d), (rough.Tool!.WaitingForTool, rough.Tool.Changing, rough.Tool.ChangeProgress));

        await AdvanceAsync(20);
        Assert.Equal((10, false), ((await RoughAsync()).Machining!.PiecesCompleted, (await RoughAsync()).Tool!.Changing));
        await AdvanceAsync(2);
        Assert.Equal(11, (await RoughAsync()).Machining!.PiecesCompleted);
    }

    [Fact]
    public async Task Small_vehicle_offline_is_an_incident_for_the_tool_delivery_only()
    {
        // 大型車在線、小型車離線：刀具配送派不出去是重大事故；和物料搬運的缺車無關。
        await AdvanceAsync(12);
        Assert.Equal(0, await DispatchAsync(Online("agv-a")));
        await AdvanceAsync(8);
        Assert.Equal(0, await DispatchAsync(Online("agv-a")));
        await AdvanceAsync(5);
        Assert.Equal(0, await DispatchAsync(Online("agv-a")));

        var summary = await SummaryAsync(Online("agv-a"));
        Assert.Equal(new[] { VehicleType.Small }, summary.OfflineVehicleTypes);
        Assert.Equal((1, 0), (summary.OfflineWaitingTasks, summary.WaitingTasks));
        Assert.Equal((0d, 5d, MachineDelayCause.WaitingForTool, ShortageReason.FleetOffline),
            (summary.Steps[0].DelaySeconds, summary.Steps[0].OfflineSeconds, summary.Steps[0].CurrentCause, summary.Steps[0].CurrentReason));
    }

    [Fact]
    public async Task With_tool_delivery_disabled_the_tool_is_changed_on_the_spot()
    {
        await ConfigureAsync(s => s.ToolDeliveryEnabled = false);
        await AdvanceAsync(20);

        var rough = await RoughAsync();
        Assert.Equal((true, false), (rough.Tool!.Changing, rough.Tool.WaitingForTool));
        await using var scope = _database.NewScope();
        Assert.Equal(0, await scope.Db.TransportTasks.CountAsync(t => t.Kind == TransportTaskKind.ToolDelivery));
    }

    /// <summary>時間前進 <paramref name="seconds"/> 秒並把加工推進到現在。</summary>
    private async Task AdvanceAsync(double seconds)
    {
        _database.Time.Advance(TimeSpan.FromSeconds(seconds));
        await using var scope = _database.NewScope();
        await scope.Production.AdvanceOperationsAsync(default);
    }

    private async Task<int> DispatchAsync(params VehicleState[] online)
    {
        await using var scope = _database.NewScope();
        return await scope.Transport.DispatchPendingAsync(online, default);
    }

    private async Task ConfigureAsync(Action<ProductionSettingsDto> change)
    {
        await using var scope = _database.NewScope();
        var settings = await scope.Production.GetSettingsAsync(default);
        change(settings);
        await scope.Production.UpdateSettingsAsync(settings, default);
    }

    private async Task<StationStatusDto> RoughAsync()
    {
        await using var scope = _database.NewScope();
        return (await scope.Production.GetStationStatusAsync(default)).Single(s => s.Code == "rough");
    }

    private async Task<VehicleShortageDto> SummaryAsync(params VehicleState[] vehicles)
    {
        await using var scope = _database.NewScope();
        return await scope.Shortage.GetSummaryAsync(vehicles, default);
    }
}
