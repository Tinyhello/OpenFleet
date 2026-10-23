using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFleet.Api.Data;
using OpenFleet.Api.Inventory;
using OpenFleet.Api.Orders;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;
using static OpenFleet.Tests.TestDatabase;

namespace OpenFleet.Tests;

/// <summary>
/// P6 第 3 步：排程預估。照現在的狀態與排程規則往後推算每台機台、每座爐的安排，以及每張訂單預估什麼時候做完、出貨、趕不趕得上交期。
/// 一律用正常速度算（使用者 2026-10-23），測試模式下另外附上加速後的預估。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScheduleForecastTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task Lathe_forecast_matches_the_machining_and_the_next_bin_waits_for_pickup_and_changeover()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await database.UseSimpleMachiningAsync();
        var upper = await CreateAsync(database, "P30-RD12-U", 30);
        var lower = await CreateAsync(database, "P30-RD12-D", 30);
        var die = await CreateAsync(database, "P30-RD12-L", 30);
        await ClearSourceBinsAsync(database);
        foreach (var order in new[] { upper, lower, die })
        {
            await ArriveAsync(database, order, "rough", 1, 30);
        }

        await AdvanceAsync(database, 0);
        var now = database.Time.GetUtcNow();
        var schedule = await ForecastAsync(database);

        // 兩台車床各做一框（每件 2 秒、30 件＝60 秒）。第三框等其中一台做完、車把做完的框取走（交接 30 秒）才開始，
        // 零件和兩台的上一框都不同，要先換線 600 秒。
        var first = Assert.Single(Machine(schedule.Normal, "rough-2").Bins);
        Assert.Equal(WorkOrder(lower), first.BatchNumber);
        Assert.True(first.Running);
        var rough1 = Machine(schedule.Normal, "rough-1").Bins;
        Assert.Equal(new[] { WorkOrder(upper), WorkOrder(die) }, rough1.Select(b => b.BatchNumber));
        AssertTime(now.AddSeconds(60), rough1[0].EndAt);
        Assert.True(rough1[1].Changeover);
        AssertTime(now.AddSeconds(90), rough1[1].StartAt);
        AssertTime(now.AddSeconds(90 + 600 + 60), rough1[1].EndAt);
        Assert.Null(schedule.Accelerated);

        // 實際的加工在預估的時間做完。
        await AdvanceAsync(database, 60);
        await using var db = database.NewContext();
        var completed = await db.Operations.Where(o => o.StationCode == "rough" && o.CompletedAt != null).Select(o => o.CompletedAt!.Value).ToListAsync();
        Assert.Equal(2, completed.Count);
        Assert.All(completed, at => AssertTime(rough1[0].EndAt, at));
    }

    [Fact]
    public async Task Rush_order_is_scheduled_first_and_finishes_earlier()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var normal = await CreateAsync(database, "P30-RD12-U", 30);
        var rush = await CreateAsync(database, "P30-RD12-D", 30);
        await ClearSourceBinsAsync(database);
        await RushAsync(database, rush.Id);
        await ArriveAsync(database, normal, "polish", 6, 30);
        await ArriveAsync(database, rush, "polish", 6, 30);

        var schedule = await ForecastAsync(database);

        // 拋光只有一台：急單後到，仍排在前面；兩張都不用經過爐，預估得出完成與出貨時間，沒有交期所以不比較。
        Assert.Equal(new[] { WorkOrder(rush), WorkOrder(normal) }, Machine(schedule.Normal, "polish-1").Bins.Select(b => b.BatchNumber));
        var rushForecast = Order(schedule.Normal, rush);
        var normalForecast = Order(schedule.Normal, normal);
        Assert.True(rushForecast.IsRush);
        Assert.True(rushForecast.FinishAt < normalForecast.FinishAt);
        Assert.True(rushForecast.ShipAt >= rushForecast.FinishAt);
        Assert.Equal(ForecastBlocker.None, normalForecast.Blocker);
        Assert.Null(normalForecast.OnTime);
    }

    [Fact]
    public async Task Small_order_waits_for_a_full_furnace_at_normal_speed_but_finishes_in_test_mode()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var order = await CreateAsync(database, "P30-RD12", 30);
        await using (var db = database.NewContext())
        {
            await db.ProductionSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.TestMode, true));
        }

        var schedule = await ForecastAsync(database);

        // 三種零件各 30 支，共 90 支：正常速度下淬火要湊滿 150 支才開爐，預估停在爐前。
        var normal = Order(schedule.Normal, order);
        Assert.Equal(ForecastBlocker.WaitingForFurnaceFill, normal.Blocker);
        Assert.Null(normal.FinishAt);
        Assert.Null(normal.OnTime);
        var heat = schedule.Normal.Furnaces.Single(f => f.StationCode == "heat");
        Assert.Empty(heat.Runs);
        Assert.Equal(90, heat.StrandedPieces);
        Assert.Equal(3, Machine(schedule.Normal, "rough-1").Bins.Count + Machine(schedule.Normal, "rough-2").Bins.Count);

        // 測試模式的規則（沒有其他料會再來就開爐）與倍率：預估得出來，而且很快。
        Assert.True(schedule.TestMode);
        var accelerated = Order(schedule.Accelerated!, order);
        Assert.Equal(ForecastBlocker.None, accelerated.Blocker);
        Assert.True(accelerated.FinishAt < schedule.GeneratedAt.AddMinutes(10));
        Assert.True(accelerated.ShipAt >= accelerated.FinishAt);
        Assert.Null(accelerated.OnTime);
    }

    [Fact]
    public async Task Due_date_is_compared_with_the_normal_speed_ship_time()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        // 讓爐有料就開，訂單才走得完整條製程。
        await using (var db = database.NewContext())
        {
            await db.ProcessSteps.Where(s => s.Kind == ProcessStepKind.Batch).ExecuteUpdateAsync(s => s.SetProperty(x => x.BatchMinPieces, 1));
        }

        var today = await CreateAsync(database, "P30-RD12", 30, new DateTime(2026, 10, 9));
        var later = await CreateAsync(database, "P30-RD12", 30, new DateTime(2026, 11, 30));

        var schedule = await ForecastAsync(database);

        // 整條製程（淬火 4 小時、回火 8 小時……）做不到當天出貨；下個月底的交期趕得上。
        var late = Order(schedule.Normal, today);
        Assert.Equal(ForecastBlocker.None, late.Blocker);
        Assert.NotNull(late.FinishAt);
        Assert.False(late.OnTime);
        Assert.True(Order(schedule.Normal, later).OnTime);
        Assert.True(schedule.Normal.Furnaces.Single(f => f.StationCode == "temper").Runs.Count > 0);
    }

    [Fact]
    public void Accelerated_forecast_opens_a_short_load_once_it_has_waited_the_test_mode_limit()
    {
        // 不經過資料庫：爐前一框 30 支已經等了 10 秒，另一框還在等料（會一直「還有料要來」）。
        var steps = new List<ProcessStep>
        {
            new() { Sequence = 1, StationCode = "rough", Kind = ProcessStepKind.Piece, SecondsPerPiece = 30 },
            new() { Sequence = 3, StationCode = "heat", Kind = ProcessStepKind.Batch, BatchMinPieces = 150, BatchMaxPieces = 300, BatchSeconds = 100 },
        };
        var settings = new ProductionSettings
        {
            SourceStationCode = "raw-store", BufferStationCode = "shipping", ToolStoreStationCode = "tool-store",
            TestFurnaceWaitSeconds = 60, TruckIntervalSeconds = 3600,
        };
        var waiting = ForecastLot("B-U01", ScheduleForecaster.LotPhase.Queued, 3, queuedAt: -10);
        var input = new ScheduleForecaster.Input
        {
            Steps = steps,
            Settings = settings,
            Machines = [],
            Runs = [],
            Lots = [waiting, ForecastLot("B-U02", ScheduleForecaster.LotPhase.WaitingForMaterial, 1)],
            Distance = (_, _) => 0,
        };

        // 測試模式的規則：再等 50 秒（滿 60 秒）開爐，100 秒後出爐。
        var accelerated = ScheduleForecaster.Run(input, new ScheduleForecaster.Rates(_ => 1, 0, 1, TestModeRules: true, TruckSpeed: 1));
        var run = Assert.Single(accelerated.Runs);
        Assert.Equal((50d, 150d, 30), (Math.Round(run.Start, 3), Math.Round(run.End, 3), run.Pieces));
        Assert.False(accelerated.Blocked.ContainsKey(waiting.Id));

        // 正常的規則：湊不到 150 支，一直停在爐前。
        var normal = ScheduleForecaster.Run(input, new ScheduleForecaster.Rates(_ => 1, 0, 1, TestModeRules: false, TruckSpeed: 1));
        Assert.Empty(normal.Runs);
        Assert.Equal(ForecastBlocker.WaitingForFurnaceFill, normal.Blocked[waiting.Id]);
    }

    private static ScheduleForecaster.LotInput ForecastLot(string number, ScheduleForecaster.LotPhase phase, int step, double queuedAt = 0) => new()
    {
        Id = Guid.NewGuid(),
        LotNumber = number,
        BatchNumber = "B",
        PartCode = ProductParts.UpperPunch,
        RodDiameterMm = 30,
        Quantity = 30,
        Step = step,
        Phase = phase,
        QueuedAt = queuedAt,
        Priority = SchedulePriority.None,
    };

    private static MachineScheduleDto Machine(ScheduleForecastDto forecast, string code) => forecast.Machines.Single(m => m.Code == code);

    private static OrderForecastDto Order(ScheduleForecastDto forecast, SalesOrderDto order) => forecast.Orders.Single(o => o.Id == order.Id);

    private static void AssertTime(DateTimeOffset expected, DateTimeOffset actual) =>
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);

    private static string WorkOrder(SalesOrderDto order) => order.Lines[0].ProductionOrders[0].Number;

    private static async Task<ScheduleDto> ForecastAsync(TestDatabase database)
    {
        await using var db = database.NewContext();
        return await new ScheduleService(db, database.Network, database.Time).GetAsync(default);
    }

    private static OrderService Orders(TestDatabase database, Scope scope) =>
        new(scope.Db, new InventoryService(scope.Db, database.Time, NullLogger<InventoryService>.Instance), scope.Production, database.Time,
            NullLogger<OrderService>.Instance);

    private static async Task<SalesOrderDto> CreateAsync(TestDatabase database, string itemCode, int quantity, DateTime? dueDate = null)
    {
        await using var scope = database.NewScope();
        var request = new CreateSalesOrderRequest
        {
            Customer = "示範客戶",
            DueDate = dueDate,
            Lines = { new CreateSalesOrderLineRequest { ItemCode = itemCode, Quantity = quantity } },
        };
        var (order, _) = await Orders(database, scope).CreateAsync(request, null, default);
        database.Time.Advance(TimeSpan.FromMilliseconds(100));
        return order;
    }

    private static async Task RushAsync(TestDatabase database, Guid orderId)
    {
        await using var scope = database.NewScope();
        await Orders(database, scope).MarkRushAsync(orderId, new RushRequest { RequestedBy = "廠長" }, default);
    }

    /// <summary>拿掉建立訂單時在原料倉建立的框與搬運任務，只留測試自己放到站上的框。</summary>
    private static async Task ClearSourceBinsAsync(TestDatabase database)
    {
        await using var db = database.NewContext();
        await db.TransportTasks.ExecuteDeleteAsync();
        await db.Lots.Where(l => l.StationCode == "raw-store").ExecuteDeleteAsync();
    }

    /// <summary>直接把一框屬於這張訂單（主工單）的料放到某站排隊，省略前面的工序。</summary>
    private static async Task ArriveAsync(TestDatabase database, SalesOrderDto order, string station, int step, int quantity)
    {
        await using var scope = database.NewScope();
        var now = database.Time.GetUtcNow();
        var workOrder = order.Lines[0].ProductionOrders[0];
        var index = await scope.Db.Lots.CountAsync(l => l.BatchNumber == workOrder.Number) + 1;
        var lot = MaterialLot.Receive(workOrder.PartCode!, quantity, station, step, workOrder.Number, 50 + index, 99, 30, now,
            workOrder.Spec.SpecCode, productionOrderId: workOrder.Id);
        lot.PlaceAt(station, now);
        scope.Db.Lots.Add(lot);
        scope.Production.OnLotArrived(lot, now);
        await scope.Db.SaveChangesAsync();
        database.Time.Advance(TimeSpan.FromMilliseconds(100));
    }

    private static async Task AdvanceAsync(TestDatabase database, double seconds)
    {
        database.Time.Advance(TimeSpan.FromSeconds(seconds));
        await using var scope = database.NewScope();
        await scope.Production.AdvanceOperationsAsync(default);
    }
}
