using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFleet.Api;
using OpenFleet.Api.Data;
using OpenFleet.Api.Inventory;
using OpenFleet.Api.Orders;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;

namespace OpenFleet.Tests;

/// <summary>
/// P5 第 5 步：訂單的出貨。開工時多做（超過 30 組多 1 組、超過 60 組多 2 組），報廢先用多做的抵；
/// 整張訂單做完才一起出貨；負責人同意可以先出一批（只出完整的組、只能分一次），剩下的到齊再出；
/// 出貨後多出來的零件依規格與客戶收進成品倉。
/// 測試直接把框放到包裝站（包好）、把箱子放到出貨站或成品倉，不經過車輛；貨車的倒數直接歸零。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FulfillmentTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Customer = "示範客戶";
    private TestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await TestDatabase.CreateAsync(postgres);

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(20, 0)]
    [InlineData(30, 0)]
    [InlineData(31, 1)]
    [InlineData(60, 1)]
    [InlineData(61, 2)]
    [InlineData(300, 2)]
    public void Extra_sets_follow_the_order_size(int quantity, int extra) => Assert.Equal(extra, ProductionOrder.ExtraFor(quantity));

    [Fact]
    public async Task Work_order_makes_extra_and_scrap_is_covered_by_it_before_a_make_up_order()
    {
        var order = await CreateAsync(("P30-RD12", 40));
        var main = order.Lines[0].ProductionOrders[0];
        Assert.Equal(1, main.ExtraQuantity);
        Assert.All(order.Lines[0].Parts, p => Assert.Equal((40, 1, 41), (p.Ordered, p.Extra, p.InProgress)));

        // 第一支報廢由多做的抵掉，不開補做工單；第二支才開 -R1。
        _database.Defects.Enqueue(DefectReason.Dimension);
        await ArriveAtInspectionAsync(main.Id);
        await AdvanceAsync(0);
        await AdvanceAsync(60);
        _database.Defects.Enqueue(DefectReason.Dimension);
        await ArriveAtInspectionAsync(main.Id);
        await AdvanceAsync(0);
        await AdvanceAsync(60);

        var line = (await OrderAsync(order.Id)).Lines[0];
        var notes = line.ProductionOrders.Single(o => o.Kind == ProductionOrderKind.Main).ScrapNotes;
        Assert.Equal([(1, (string?)null), (0, $"{main.Number}-R1")], notes.Select(n => (n.CoveredByExtra, n.MakeupOrderNumber)));
        var makeup = Assert.Single(line.ProductionOrders, o => o.Kind == ProductionOrderKind.Makeup);
        Assert.Equal((ProductParts.UpperPunch, 1), (makeup.PartCode, makeup.Quantity));
        var upper = line.Parts.Single(p => p.PartCode == ProductParts.UpperPunch);
        Assert.Equal((2, 1), (upper.Scrapped, upper.Makeup));
    }

    [Fact]
    public async Task Checking_whether_an_order_can_be_boxed_does_not_hold_its_moving_bins()
    {
        // 上沖已經包好、中模在車上。檢查訂單能不能裝箱時，中模只讀狀態、不留在這一輪的工作單元裡：
        // 否則它在同一輪裡被卸到下一站，這一輪後面的步驟（空機台開始下一框）會拿到舊的「在車上」而失敗。
        var order = await CreateAsync(("P30-RD12", 30));
        await PackAsync(order, 1, lot => lot.PartCode == ProductParts.UpperPunch);
        Guid moving;
        await using (var db = _database.NewContext())
        {
            var die = await db.Lots.SingleAsync(l => l.PartCode == ProductParts.Die);
            die.LoadOnto("agv-a", _database.Time.GetUtcNow());
            await db.SaveChangesAsync();
            moving = die.Id;
        }

        await using var scope = _database.NewScope();
        await scope.Production.AdvanceOperationsAsync(default);
        Assert.DoesNotContain(scope.Db.ChangeTracker.Entries<MaterialLot>(), entry => entry.Entity.Id == moving);
    }

    [Fact]
    public async Task Whole_order_is_boxed_only_when_every_line_is_done_and_ships_when_all_boxes_are_there()
    {
        var order = await CreateAsync(("P30-RD12", 10), ("P24-SQ08-U", 5));

        // 第 1 行包好了，第 2 行還在做：還不裝箱。
        await PackAsync(order, line: 1);
        await AdvanceAsync(0);
        Assert.Empty((await OrderAsync(order.Id)).Deliveries);

        // 第 2 行也包好：整張訂單裝一次，每一行每種零件一箱。
        await PackAsync(order, line: 2);
        await AdvanceAsync(0);
        var delivery = Assert.Single((await OrderAsync(order.Id)).Deliveries);
        Assert.False(delivery.IsPartial);
        Assert.Equal([(1, ProductParts.UpperPunch, 10), (1, ProductParts.Die, 10), (1, ProductParts.LowerPunch, 10), (2, ProductParts.UpperPunch, 5)],
            delivery.Lines.Select(l => (l.LineNumber, l.PartCode, l.Quantity)));
        Assert.Equal(SalesOrderStatus.Completed, (await OrderAsync(order.Id)).Status);

        // 只到了三箱：貨車不載。
        await DeliverBoxesAsync("shipping", box => !box.LotNumber.EndsWith("-2U"));
        await TruckArrivesAsync();
        Assert.Null((await OrderAsync(order.Id)).Deliveries.Single().ShippedAt);

        // 四箱都到了：下一班整張載走。
        await DeliverBoxesAsync("shipping", _ => true);
        await TruckArrivesAsync();
        var shipped = await OrderAsync(order.Id);
        Assert.Equal(SalesOrderStatus.Shipped, shipped.Status);
        Assert.NotNull(shipped.Deliveries.Single().ShippedAt);
        Assert.All(shipped.Lines.SelectMany(l => l.Parts), p => Assert.Equal(p.Ordered, p.Shipped));

        await using var db = _database.NewContext();
        Assert.Equal([delivery.Number], (await db.Shipments.AsNoTracking().Select(s => s.BatchNumber).ToListAsync()).Distinct());
    }

    [Fact]
    public async Task Approved_partial_ships_complete_sets_once_and_the_rest_ships_whole_with_surplus_to_the_store()
    {
        // 40 組 → 每種多做 1 支，各 41 支，分成 30、11 兩框。
        var order = await CreateAsync(("P30-RD12", 40));

        // 上沖、中模都包好了 41 支，下沖只包好第一框 30 支：完整的組是 30 組。
        await PackAsync(order, line: 1, which: lot => lot.PartCode != ProductParts.LowerPunch || lot.BinIndex == 1);
        var approved = await ApproveAsync(order.Id, "廠長");
        Assert.Equal(("廠長", SalesOrderStatus.InProduction), (approved.PartialApprovedBy, approved.Status));
        await Assert.ThrowsAsync<ConflictException>(() => ApproveAsync(order.Id, "廠長"));

        await AdvanceAsync(0);
        var partial = Assert.Single((await OrderAsync(order.Id)).Deliveries);
        Assert.True(partial.IsPartial);
        Assert.All(partial.Lines, l => Assert.Equal(30, l.Quantity));

        await DeliverBoxesAsync("shipping", _ => true);
        await TruckArrivesAsync();
        var afterPartial = await OrderAsync(order.Id);
        Assert.Equal(SalesOrderStatus.PartiallyShipped, afterPartial.Status);
        Assert.All(afterPartial.Lines[0].Parts, p => Assert.Equal(30, p.Shipped));

        // 剩下的下沖也包好：整張出貨剩下的 10 組；每種多出來的 1 支裝箱送成品倉。
        await PackAsync(order, line: 1);
        await AdvanceAsync(0);
        var final = (await OrderAsync(order.Id)).Deliveries.Single(d => !d.IsPartial);
        Assert.All(final.Lines, l => Assert.Equal(10, l.Quantity));

        await using (var db = _database.NewContext())
        {
            var surplusBoxes = await db.Lots.AsNoTracking().Where(l => l.IsBox && l.DeliveryId == null).ToListAsync();
            Assert.Equal(3, surplusBoxes.Count);
            Assert.All(surplusBoxes, b => Assert.Equal(1, b.Quantity));
            Assert.Equal(3, await db.TransportTasks.CountAsync(t => t.ToStationCode == ProductionService.FinishedStoreStation));
            // 上沖的第二框只要 10 支：分出 10 支裝箱，剩的 1 支送成品倉。
            Assert.True(await db.Lots.AnyAsync(l => l.LotNumber.EndsWith("-U02-P") && l.Quantity == 10 && l.Status == LotStatus.Boxed));
        }

        await DeliverBoxesAsync(ProductionService.FinishedStoreStation, box => box.DeliveryId == null);
        await AdvanceAsync(0);
        await DeliverBoxesAsync("shipping", _ => true);
        await TruckArrivesAsync();

        var shipped = await OrderAsync(order.Id);
        Assert.Equal(SalesOrderStatus.Shipped, shipped.Status);
        Assert.All(shipped.Lines[0].Parts, p => Assert.Equal((40, 41, 40, 1), (p.Ordered, p.Finished, p.Shipped, p.Surplus)));

        // 成品倉依規格與客戶記庫存。
        await using var read = _database.NewContext();
        var inventory = new InventoryService(read, _database.Time, NullLogger<InventoryService>.Instance);
        var stored = (await inventory.GetSummaryAsync(default)).Balances.Where(b => b.StationCode == ProductionService.FinishedStoreStation).ToList();
        Assert.Equal([("P30-RD12-U", 1), ("P30-RD12-D", 1), ("P30-RD12-L", 1)], stored.Select(b => (b.ItemCode, b.OnHand)));
        Assert.All(stored, b => Assert.Equal(Customer, b.Owner));
    }

    [Fact]
    public async Task Partial_shipment_needs_a_name_a_complete_set_and_an_unfinished_order()
    {
        var waiting = await CreateAsync(("P30-RD12", 10));
        await Assert.ThrowsAsync<RequestValidationException>(() => ApproveAsync(waiting.Id, " "));
        await Assert.ThrowsAsync<ConflictException>(() => ApproveAsync(waiting.Id, "廠長"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ApproveAsync(Guid.NewGuid(), "廠長"));

        // 全部做完的訂單本來就整張出貨，不用同意分批。
        var complete = await CreateAsync(("P30-RD12", 5));
        await PackAsync(complete, line: 1);
        await Assert.ThrowsAsync<ConflictException>(() => ApproveAsync(complete.Id, "廠長"));

        // 兩個人同時按同意：只有一個成功。
        var busy = await CreateAsync(("P24-SQ08", 50));
        await PackAsync(busy, line: 1, which: lot => lot.BinIndex == 1);
        var results = await Task.WhenAll(TryApproveAsync(busy.Id, "廠長"), TryApproveAsync(busy.Id, "副廠長"));
        Assert.Single(results, ok => ok);

        async Task<bool> TryApproveAsync(Guid id, string name)
        {
            try
            {
                await ApproveAsync(id, name);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }
    }

    private OrderService NewOrders(TestDatabase.Scope scope) =>
        new(scope.Db, new InventoryService(scope.Db, _database.Time, NullLogger<InventoryService>.Instance), scope.Production, _database.Time,
            NullLogger<OrderService>.Instance);

    private async Task<SalesOrderDto> CreateAsync(params (string Item, int Quantity)[] lines)
    {
        await using var scope = _database.NewScope();
        var request = new CreateSalesOrderRequest { Customer = Customer };
        request.Lines.AddRange(lines.Select(l => new CreateSalesOrderLineRequest { ItemCode = l.Item, Quantity = l.Quantity }));
        var (order, _) = await NewOrders(scope).CreateAsync(request, null, default);
        _database.Time.Advance(TimeSpan.FromMilliseconds(100));
        return order;
    }

    private async Task<SalesOrderDto> OrderAsync(Guid id)
    {
        await using var scope = _database.NewScope();
        return (await NewOrders(scope).GetAsync(id, default))!;
    }

    private async Task<SalesOrderDto> ApproveAsync(Guid id, string name)
    {
        await using var scope = _database.NewScope();
        return await NewOrders(scope).ApprovePartialShipmentAsync(id, new PartialShipmentRequest { ApprovedBy = name }, default);
    }

    /// <summary>把訂單某一行（主工單）還沒包好的框直接放到包裝站、包好。</summary>
    private async Task PackAsync(SalesOrderDto order, int line, Func<MaterialLot, bool>? which = null)
    {
        var orderId = order.Lines.Single(l => l.LineNumber == line).ProductionOrders.Single(o => o.Kind == ProductionOrderKind.Main).Id;
        await using var db = _database.NewContext();
        var lots = (await db.Lots.Where(l => l.ProductionOrderId == orderId && !l.IsBox
                && l.Status != LotStatus.Packed && l.Status != LotStatus.Boxed).ToListAsync())
            .Where(which ?? (_ => true));
        foreach (var lot in lots)
        {
            var entry = db.Entry(lot);
            entry.Property(l => l.Status).CurrentValue = LotStatus.Packed;
            entry.Property(l => l.StationCode).CurrentValue = "packing-1";
            entry.Property(l => l.VehicleId).CurrentValue = null;
            entry.Property(l => l.CurrentStepSequence).CurrentValue = null;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>把包裝站上等待搬運的箱子直接放到 <paramref name="station"/>（和車輛卸料一樣）。</summary>
    private async Task DeliverBoxesAsync(string station, Func<MaterialLot, bool> which)
    {
        await using var db = _database.NewContext();
        var now = _database.Time.GetUtcNow();
        var boxes = (await db.Lots.Where(l => l.IsBox && l.Status == LotStatus.AwaitingTransport && l.StationCode == "packing-1").ToListAsync()).Where(which);
        foreach (var box in boxes)
        {
            box.LoadOnto("agv-a", now);
            box.UnloadAt(station, now);
        }

        await db.SaveChangesAsync();
    }

    private async Task TruckArrivesAsync()
    {
        await using (var db = _database.NewContext())
        {
            await db.TruckSchedules.ExecuteUpdateAsync(s => s.SetProperty(t => t.RemainingSeconds, 0d));
        }

        await AdvanceAsync(0);
    }

    private async Task ArriveAtInspectionAsync(Guid orderId)
    {
        await using var scope = _database.NewScope();
        var now = _database.Time.GetUtcNow();
        var qcStep = (await scope.Db.ProcessSteps.SingleAsync(s => s.IsInspection)).Sequence;
        var batch = await scope.Db.ProductionOrders.Where(o => o.Id == orderId).Select(o => o.Number).SingleAsync();
        var index = await scope.Db.Lots.CountAsync(l => l.BatchNumber == batch) + 1;
        var lot = MaterialLot.Receive(ProductParts.UpperPunch, 1, "qc-1", qcStep, batch, 90 + index, 99, 30, now, "P30-RD12", productionOrderId: orderId);
        lot.PlaceAt("qc-1", now);
        scope.Db.Lots.Add(lot);
        scope.Production.OnLotArrived(lot, now);
        await scope.Db.SaveChangesAsync();
        _database.Time.Advance(TimeSpan.FromMilliseconds(100));
    }

    private async Task AdvanceAsync(double seconds)
    {
        _database.Time.Advance(TimeSpan.FromSeconds(seconds));
        await using var scope = _database.NewScope();
        await scope.Production.AdvanceOperationsAsync(default);
    }
}
