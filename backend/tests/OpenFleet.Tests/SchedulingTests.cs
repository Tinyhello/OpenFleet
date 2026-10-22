using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFleet.Api;
using OpenFleet.Api.Data;
using OpenFleet.Api.Inventory;
using OpenFleet.Api.Orders;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;
using static OpenFleet.Tests.TestDatabase;

namespace OpenFleet.Tests;

/// <summary>
/// P6 第 2 步：急單、交期優先（使用者 2026-10-22）。急單排在所有訂單前面，其餘交期早的先，沒有交期的最後；
/// 套用在各站挑下一框、開爐裝爐、分配圓棒與派車。測試裡的訂單都是只做一種零件的單行訂單（一框）。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SchedulingTests(PostgresFixture postgres)
{
    private const string Bar = "BAR-D40";

    [Fact]
    public async Task Rush_bin_starts_on_a_lathe_ahead_of_bins_that_arrived_earlier()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var first = await CreateAsync(database, "P30-RD12-U", 30);
        var second = await CreateAsync(database, "P30-RD12-D", 30);
        var rush = await CreateAsync(database, "P30-RD12-L", 30);
        await RushAsync(database, rush.Id, "廠長");

        // 到站順序：一般、一般、急單。粗加工兩台都空著：急單和最先到的一般訂單先做，第二張一般訂單排隊。
        foreach (var order in new[] { first, second, rush })
        {
            await ArriveAsync(database, order, "rough", 1, 30);
        }

        await AdvanceAsync(database, 0);
        await using var db = database.NewContext();
        var running = await db.Operations.Where(o => o.Status == OperationStatus.Running).Select(o => o.Lot.BatchNumber).ToListAsync();
        Assert.Equal(new[] { WorkOrder(first), WorkOrder(rush) }.Order(), running.Order());
        Assert.Equal(WorkOrder(second), await db.Operations.Where(o => o.Status == OperationStatus.Queued).Select(o => o.Lot.BatchNumber).SingleAsync());
    }

    [Fact]
    public async Task Earlier_due_date_goes_first_and_orders_without_a_due_date_go_last()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var none = await CreateAsync(database, "P30-RD12-U", 30);
        var later = await CreateAsync(database, "P30-RD12-D", 30, new DateTime(2026, 10, 19));
        var soon = await CreateAsync(database, "P30-RD12-L", 30, new DateTime(2026, 10, 14));

        // 拋光只有一台：沒有交期的最先到、交期晚的其次、交期早的最後到，先做交期早的。
        foreach (var order in new[] { none, later, soon })
        {
            await ArriveAsync(database, order, "polish", 6, 1);
        }

        await AdvanceAsync(database, 0);
        Assert.Equal(WorkOrder(soon), await RunningAtAsync(database, "polish"));

        // 做完、搬走之後，換交期晚的那張；沒有交期的最後。
        await PickUpAsync(database, "polish", 60);
        Assert.Equal(WorkOrder(later), await RunningAtAsync(database, "polish"));
        await PickUpAsync(database, "polish", 60);
        Assert.Equal(WorkOrder(none), await RunningAtAsync(database, "polish"));
    }

    [Fact]
    public async Task Furnace_loads_rush_bins_before_bins_that_arrived_earlier()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var normal = await CreateAsync(database, "P30-RD12-U", 30);
        var rush = await CreateAsync(database, "P30-RD12-D", 30);
        await RushAsync(database, rush.Id, "廠長");

        // 淬火一爐最多 300 支：一般訂單的 10 框（300 支）先到，急單的一框最後到。急單的框裝進這一爐，一般訂單留一框到下一爐。
        for (var i = 0; i < 10; i++)
        {
            await ArriveAsync(database, normal, "heat", 3, 30);
        }

        await ArriveAsync(database, rush, "heat", 3, 30);
        await AdvanceAsync(database, 0);

        await using var db = database.NewContext();
        var inFurnace = await db.Operations.Where(o => o.Status == OperationStatus.Running && o.StationCode == "heat")
            .Select(o => o.Lot.BatchNumber).ToListAsync();
        Assert.Equal(10, inFurnace.Count);
        Assert.Contains(WorkOrder(rush), inFurnace);
        Assert.Equal(WorkOrder(normal), await db.Operations.Where(o => o.Status == OperationStatus.Queued && o.StationCode == "heat")
            .Select(o => o.Lot.BatchNumber).SingleAsync());
    }

    [Fact]
    public async Task Rush_bins_get_the_bars_first_when_bars_are_short()
    {
        // 示範庫存沒有六角 10 的備料，要從圓棒切；圓棒先清空，兩張單都在原料倉等料。
        var database = await TestDatabase.CreateAsync(postgres, plentyOfBlanks: false);
        await AdjustBarsAsync(database, -40);
        var normal = await CreateAsync(database, "P30-HX10-U", 13);
        var rush = await CreateAsync(database, "P30-HX10-L", 13);
        await RushAsync(database, rush.Id, "廠長");

        // 補進 1 根：一根切得完 13 支，給急單（較晚建立的那一張）。
        await AdjustBarsAsync(database, 1);
        await AdvanceAsync(database, 0);

        await using var db = database.NewContext();
        var statuses = await db.Lots.AsNoTracking().ToDictionaryAsync(l => l.BatchNumber, l => l.Status);
        Assert.Equal((LotStatus.AwaitingTransport, LotStatus.AwaitingMaterial), (statuses[WorkOrder(rush)], statuses[WorkOrder(normal)]));
    }

    [Fact]
    public async Task Within_the_same_kind_of_transport_rush_and_earlier_due_dates_are_carried_first()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var none = await CreateAsync(database, "P30-RD12-U", 10);
        var due = await CreateAsync(database, "P30-RD12-D", 10, new DateTime(2026, 10, 20));
        var rush = await CreateAsync(database, "P30-RD12-L", 10);
        await RushAsync(database, rush.Id, "廠長");

        // 三趟都是補料（原料倉 → 粗加工）：一次只派一台，依序是急單、有交期的、沒有交期的。
        foreach (var expected in new[] { rush, due, none })
        {
            Assert.Equal(WorkOrder(expected), await DispatchOneAsync(database));
        }

        // 搬運種類的順序不變：一般訂單做完的半成品，還是比急單的補料先搬（使用者 2026-10-09）。
        var rushAgain = await CreateAsync(database, "P30-RD12-L", 10);
        await RushAsync(database, rushAgain.Id, "廠長");
        await using (var db = database.NewContext())
        {
            var now = database.Time.GetUtcNow();
            var finished = MaterialLot.Receive("upper-punch", 10, "rough", 2, WorkOrder(none), 9, 9, 30, now, "P30-RD12",
                productionOrderId: none.Lines[0].ProductionOrders[0].Id);
            db.Lots.Add(finished);
            db.TransportTasks.Add(TransportTask.Create(finished, "rough", "fine", TransportTaskKind.WipTransfer, now));
            await db.SaveChangesAsync();
        }

        Assert.Equal(WorkOrder(none), await DispatchOneAsync(database));
        Assert.Equal(WorkOrder(rushAgain), await DispatchOneAsync(database));
    }

    [Fact]
    public async Task Person_in_charge_marks_and_cancels_a_rush_order_but_not_a_finished_one()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var order = await CreateAsync(database, "P30-RD12-U", 10);

        // 設成急單：記下負責人與時間；已經是急單時不能再設。
        var rushed = await RushAsync(database, order.Id, "  廠長 ");
        Assert.Equal((true, "廠長", database.Time.GetUtcNow()), (rushed.IsRush, rushed.RushRequestedBy, rushed.RushRequestedAt));
        await Assert.ThrowsAsync<ConflictException>(() => RushAsync(database, order.Id, "副廠長"));

        // 取消之後回到一般訂單；不是急單時不能取消。
        await using (var scope = database.NewScope())
        {
            var cancelled = await Orders(database, scope).CancelRushAsync(order.Id, default);
            Assert.Equal((false, null, null), (cancelled.IsRush, cancelled.RushRequestedBy, cancelled.RushRequestedAt));
            await Assert.ThrowsAsync<ConflictException>(() => Orders(database, scope).CancelRushAsync(order.Id, default));
        }

        // 一定要有負責人；找不到的訂單是 404。
        await Assert.ThrowsAsync<RequestValidationException>(() => RushAsync(database, order.Id, " "));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => RushAsync(database, Guid.NewGuid(), "廠長"));

        // 全部包好（做完了）的訂單不用再設急單。
        await using (var db = database.NewContext())
        {
            foreach (var lot in await db.Lots.ToListAsync())
            {
                var entry = db.Entry(lot);
                entry.Property(l => l.Status).CurrentValue = LotStatus.Packed;
                entry.Property(l => l.StationCode).CurrentValue = "packing";
                entry.Property(l => l.CurrentStepSequence).CurrentValue = null;
            }

            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ConflictException>(() => RushAsync(database, order.Id, "廠長"));
    }

    private static string WorkOrder(SalesOrderDto order) => order.Lines[0].ProductionOrders[0].Number;

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

    private static async Task<SalesOrderDto> RushAsync(TestDatabase database, Guid orderId, string requestedBy)
    {
        await using var scope = database.NewScope();
        return await Orders(database, scope).MarkRushAsync(orderId, new RushRequest { RequestedBy = requestedBy }, default);
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

    private static async Task<string?> RunningAtAsync(TestDatabase database, string station)
    {
        await using var db = database.NewContext();
        return await db.Operations.Where(o => o.Status == OperationStatus.Running && o.StationCode == station)
            .Select(o => o.Lot.BatchNumber).SingleOrDefaultAsync();
    }

    /// <summary>等這一站正在做的框做完，車把它取走（取料完成），再推進一輪讓機台接下一框。</summary>
    private static async Task PickUpAsync(TestDatabase database, string station, double seconds)
    {
        await AdvanceAsync(database, seconds);
        await using (var db = database.NewContext())
        {
            var done = await db.Lots.SingleAsync(l => l.StationCode == station && l.Status == LotStatus.AwaitingTransport);
            done.LoadOnto("agv-a", database.Time.GetUtcNow());
            await db.SaveChangesAsync();
        }

        await AdvanceAsync(database, 0);
    }

    /// <summary>只有一台車有空：派一趟並送到，回傳那一框的批次（工單號碼）。</summary>
    private static async Task<string> DispatchOneAsync(TestDatabase database)
    {
        string batch;
        await using (var scope = database.NewScope())
        {
            Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online("agv-a")], default));
            batch = await scope.Db.Assignments.Where(a => a.VehicleId == "agv-a" && a.Status == AssignmentStatus.Dispatched)
                .Select(a => a.Task.Lot!.BatchNumber).SingleAsync();
        }

        await database.CompleteDeliveryAsync("agv-a");
        database.Time.Advance(TimeSpan.FromMilliseconds(100));
        return batch;
    }

    private static async Task AdjustBarsAsync(TestDatabase database, int delta)
    {
        await using var db = database.NewContext();
        var inventory = new InventoryService(db, database.Time, NullLogger<InventoryService>.Instance);
        await inventory.AdjustAsync(new StockAdjustmentRequest { ItemCode = Bar, Quantity = delta }, default);
    }
}
