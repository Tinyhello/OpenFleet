using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFleet.Api;
using OpenFleet.Api.Catalog;
using OpenFleet.Api.Data;
using OpenFleet.Api.Inventory;
using OpenFleet.Api.Orders;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;

namespace OpenFleet.Tests;

/// <summary>
/// P5 第 4 步：客戶訂單與生產工單。一行明細開一張主工單，記下開立時的規格與 BOM 版本並建立它的框；
/// 同一個 Idempotency-Key 只會有一張訂單；報廢記在主工單上，另開補做工單（補做又報廢再開一張，都連回主工單）。
/// 示範庫存：圓棒 40 根、P30-RD12 三種備料各 40 支。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrderTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTime DueDate = new(2026, 11, 30);
    private TestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await TestDatabase.CreateAsync(postgres, plentyOfBlanks: false);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Order_opens_one_main_work_order_per_line_with_the_spec_and_bom_it_used()
    {
        var (order, created) = await CreateAsync(new CreateSalesOrderRequest
        {
            Customer = "示範客戶",
            DueDate = DueDate,
            Lines =
            {
                new CreateSalesOrderLineRequest { ItemCode = "P30-HX10", Quantity = 40 },
                new CreateSalesOrderLineRequest { ItemCode = "p24-sq08-d", Quantity = 10 },
            },
        });

        Assert.True(created);
        Assert.StartsWith("SO-20261009-", order.Number);
        Assert.Equal(("示範客戶", DueDate, SalesOrderStatus.InProduction), (order.Customer, order.DueDate, order.Status));

        var set = order.Lines[0];
        var setOrder = Assert.Single(set.ProductionOrders);
        Assert.Equal(($"WO-{order.Number[3..]}-1", ProductionOrderKind.Main, "P30-HX10", (string?)null, 40, 1),
            (setOrder.Number, setOrder.Kind, setOrder.ItemCode, setOrder.PartCode, setOrder.Quantity, setOrder.BomVersion));
        // 六角 10：精加工覆寫成 90 秒、粗加工沒有覆寫；備料長度沿用 Φ30。
        Assert.Equal((30, 30, 38, 90d, (double?)null),
            (setOrder.Spec.RodDiameterMm, setOrder.Spec.PiecesPerBin, setOrder.Spec.DieOuterDiameterMm, setOrder.Spec.FinishSecondsPerPiece, setOrder.Spec.RoughSecondsPerPiece));
        Assert.Equal([110, 90, 95], ProductParts.All.Select(p => setOrder.Spec.BlankLengthMm[p]));

        // 單一零件的明細：只做那一種，代碼不分大小寫。
        var die = order.Lines[1];
        Assert.Equal(("P24-SQ08-D", ItemKind.Part), (die.ItemCode, die.Kind));
        var dieOrder = Assert.Single(die.ProductionOrders);
        Assert.Equal((ProductParts.Die, 10, 24, 45), (dieOrder.PartCode, dieOrder.Quantity, dieOrder.Spec.RodDiameterMm, dieOrder.Spec.PiecesPerBin));
        Assert.Equal([ProductParts.Die], die.Parts.Select(p => p.PartCode));

        // 工單的框：批次編號就是工單號碼。40 組多做 1 組；HX10 沒有現成備料，每種 41 支分成 30、11 兩框去切。
        await using var db = _database.NewContext();
        var lots = await db.Lots.AsNoTracking().Where(l => l.ProductionOrderId == setOrder.Id).ToListAsync();
        Assert.Equal(6, lots.Count);
        Assert.All(lots, l => Assert.Equal((setOrder.Number, "P30-HX10", 0), (l.BatchNumber, l.SpecCode, l.CurrentStepSequence!.Value)));
        Assert.Equal(10, await db.Lots.Where(l => l.ProductionOrderId == dieOrder.Id).SumAsync(l => l.Quantity));

        // 剛開立：全部在製作中（含多做的 1 支），還沒有完成的。
        Assert.Equal(1, setOrder.ExtraQuantity);
        Assert.All(set.Parts, p => Assert.Equal((40, 1, 0, 41, 0, 0), (p.Ordered, p.Extra, p.Finished, p.InProgress, p.Scrapped, p.Makeup)));
    }

    [Fact]
    public async Task Same_idempotency_key_gives_one_order_even_when_sent_twice_at_once()
    {
        var request = new CreateSalesOrderRequest { Customer = "重送測試", Lines = { new CreateSalesOrderLineRequest { ItemCode = "P30-RD12", Quantity = 20 } } };

        var results = await Task.WhenAll(CreateAsync(request, "order-1"), CreateAsync(request, "order-1"));

        Assert.Single(results, r => r.Created);
        Assert.Equal(results[0].Order.Id, results[1].Order.Id);
        var replay = await CreateAsync(request, "order-1");
        Assert.Equal((results[0].Order.Id, false), (replay.Order.Id, replay.Created));

        // 只有一張訂單、一張工單、一組框；現成的備料只被用掉一次。
        await using var db = _database.NewContext();
        Assert.Equal((1, 1, 3), (await db.SalesOrders.CountAsync(), await db.ProductionOrders.CountAsync(), await db.Lots.CountAsync()));
        var inventory = new InventoryService(db, _database.Time, NullLogger<InventoryService>.Instance);
        Assert.Equal(20, (await inventory.GetSummaryAsync(default)).Balances.Single(b => b.ItemCode == "P30-RD12-U-BLK").OnHand);

        await Assert.ThrowsAsync<ConflictException>(() => CreateAsync(
            new CreateSalesOrderRequest { Customer = "重送測試", Lines = { new CreateSalesOrderLineRequest { ItemCode = "P30-RD12", Quantity = 21 } } },
            "order-1"));
    }

    [Fact]
    public async Task Invalid_orders_are_rejected()
    {
        CreateSalesOrderRequest Order(string itemCode, int quantity, string customer = "客戶", DateTime? due = null) =>
            new() { Customer = customer, DueDate = due, Lines = { new CreateSalesOrderLineRequest { ItemCode = itemCode, Quantity = quantity } } };

        await Invalid(Order("P30-RD12", 10, customer: " "));
        await Invalid(new CreateSalesOrderRequest { Customer = "客戶" });
        await Invalid(new CreateSalesOrderRequest
        {
            Customer = "客戶",
            Lines = Enumerable.Range(0, OrderService.MaxLines + 1).Select(_ => new CreateSalesOrderLineRequest { ItemCode = "P30-RD12", Quantity = 1 }).ToList(),
        });
        await Invalid(Order("P30-RD12-U-BLK", 10));
        await Invalid(Order("BAR-D40", 10));
        await Invalid(Order("NOPE", 10));
        await Invalid(Order("P30-RD12", 0));
        await Invalid(Order("P30-RD12", 301));
        await Invalid(Order("P30-RD12", 10, due: new DateTime(2026, 10, 8)));

        await using var db = _database.NewContext();
        Assert.Equal(0, await db.SalesOrders.CountAsync());

        async Task Invalid(CreateSalesOrderRequest request) =>
            await Assert.ThrowsAsync<RequestValidationException>(() => CreateAsync(request));
    }

    [Fact]
    public async Task Work_order_keeps_the_spec_it_was_opened_with_even_for_later_make_up()
    {
        var (order, _) = await CreateAsync(new CreateSalesOrderRequest
        {
            Customer = "規格快照", Lines = { new CreateSalesOrderLineRequest { ItemCode = "P24-SQ08", Quantity = 10 } },
        });
        var main = order.Lines[0].ProductionOrders[0];

        // 開立之後才改 Φ24 的上沖備料長度：規格的現值變了，工單存下的不變。
        await using (var db = _database.NewContext())
        {
            await db.RodSizes.Where(r => r.DiameterMm == 24).ExecuteUpdateAsync(s => s.SetProperty(r => r.UpperBlankLengthMm, 120));
            var catalog = new CatalogService(db, _database.Time, NullLogger<CatalogService>.Instance);
            Assert.Equal(120, (await catalog.GetSpecAsync("P24-SQ08", default))!.BlankLengthMm[ProductParts.UpperPunch].Value);
        }

        Assert.Equal(100, (await ProductionOrderAsync(main.Id)).Spec.BlankLengthMm[ProductParts.UpperPunch]);

        // 這張工單的料在品管報廢 1 支：補做的料照工單的規格切（100 mm，一根 1500 ÷ 103 = 14 支），不是改過的 120 mm。
        _database.Defects.Enqueue(DefectReason.Dimension);
        await ArriveAtInspectionAsync(main.Id, "P24-SQ08", ProductParts.UpperPunch);
        await AdvanceAsync(0);
        await AdvanceAsync(60);
        await AdvanceAsync(0);

        await using var read = _database.NewContext();
        var makeupLot = await read.Lots.AsNoTracking().SingleAsync(l => l.IsMakeup);
        var job = await read.CuttingJobs.AsNoTracking().SingleAsync(j => j.LotId == makeupLot.Id);
        Assert.Equal((100, 14, 1), (job.BlankLengthMm, job.PiecesPerBar, job.Bars));
    }

    [Fact]
    public async Task Machining_time_uses_the_work_order_spec_override()
    {
        var (hex, _) = await CreateAsync(new CreateSalesOrderRequest
        {
            Customer = "加工時間", Lines = { new CreateSalesOrderLineRequest { ItemCode = "P30-HX10-U", Quantity = 1 } },
        });
        var (round, _) = await CreateAsync(new CreateSalesOrderRequest
        {
            Customer = "加工時間", Lines = { new CreateSalesOrderLineRequest { ItemCode = "P30-RD12-U", Quantity = 1 } },
        });

        // 六角 10 覆寫了精加工（90 秒），圓形 12 沒有（沿用製程設定的 60 秒）；粗加工兩個都沿用 30 秒。
        Assert.Equal(90d, await StartAsync(hex.Lines[0].ProductionOrders[0].Id, "P30-HX10", "fine"));
        Assert.Equal(60d, await StartAsync(round.Lines[0].ProductionOrders[0].Id, "P30-RD12", "fine"));
        Assert.Equal(30d, await StartAsync(hex.Lines[0].ProductionOrders[0].Id, "P30-HX10", "rough"));

        async Task<double> StartAsync(Guid orderId, string spec, string station)
        {
            int step;
            await using (var db = _database.NewContext())
            {
                step = (await db.ProcessSteps.SingleAsync(s => s.StationCode == station)).Sequence;
            }

            var lotId = await ArriveAsync(orderId, spec, ProductParts.UpperPunch, station, step);
            await AdvanceAsync(0);
            double seconds;
            await using (var read = _database.NewContext())
            {
                seconds = (await read.Operations.AsNoTracking().SingleAsync(o => o.LotId == lotId)).SecondsPerPiece!.Value;
            }

            // 做完後把這一框搬離機台（做完的框沒搬走，機台不會接下一框）。
            await AdvanceAsync(seconds + 20);
            await using var scope = _database.NewScope();
            var lot = await scope.Db.Lots.SingleAsync(l => l.Id == lotId);
            lot.LoadOnto("agv-a", _database.Time.GetUtcNow());
            await scope.Db.SaveChangesAsync();
            return seconds;
        }
    }

    [Fact]
    public async Task Scrap_is_noted_on_the_main_order_and_every_scrap_opens_a_new_make_up_order()
    {
        var (order, _) = await CreateAsync(new CreateSalesOrderRequest
        {
            Customer = "補做", Lines = { new CreateSalesOrderLineRequest { ItemCode = "P30-RD12", Quantity = 10 } },
        });
        var main = order.Lines[0].ProductionOrders[0];

        // 主工單的上沖報廢 1 支 → 補做工單 -R1。
        _database.Defects.Enqueue(DefectReason.Dimension);
        await ArriveAtInspectionAsync(main.Id, "P30-RD12", ProductParts.UpperPunch);
        await AdvanceAsync(0);
        await AdvanceAsync(60);

        var first = (await OrderAsync(order.Id)).Lines[0].ProductionOrders.Single(o => o.Kind == ProductionOrderKind.Makeup);
        Assert.Equal(($"{main.Number}-R1", main.Number, ProductParts.UpperPunch, "P30-RD12-U", 1),
            (first.Number, first.ParentNumber, first.PartCode, first.ItemCode, first.Quantity));

        // 補做工單的料又報廢 1 支 → 再開 -R2，一樣連回主工單、記在主工單上。
        _database.Defects.Enqueue(DefectReason.Dimension);
        await ArriveAtInspectionAsync(first.Id, "P30-RD12", ProductParts.UpperPunch);
        await AdvanceAsync(0);
        await AdvanceAsync(60);

        var line = (await OrderAsync(order.Id)).Lines[0];
        var makeups = line.ProductionOrders.Where(o => o.Kind == ProductionOrderKind.Makeup).ToList();
        Assert.Equal([$"{main.Number}-R1", $"{main.Number}-R2"], makeups.Select(o => o.Number));
        Assert.All(makeups, o => Assert.Equal(main.Number, o.ParentNumber));

        var notes = line.ProductionOrders.Single(o => o.Kind == ProductionOrderKind.Main).ScrapNotes;
        Assert.Equal([(main.Number, $"{main.Number}-R1"), ($"{main.Number}-R1", $"{main.Number}-R2")],
            notes.Select(n => (n.SourceOrderNumber, n.MakeupOrderNumber)));

        var upper = line.Parts.Single(p => p.PartCode == ProductParts.UpperPunch);
        Assert.Equal((2, 2), (upper.Scrapped, upper.Makeup));

        // 補做的框屬於補做工單，批次編號仍是主工單的號碼（同一張主工單的料一起裝箱）。
        await using var db = _database.NewContext();
        var makeupLots = await db.Lots.AsNoTracking().Where(l => l.IsMakeup).OrderBy(l => l.LotNumber).ToListAsync();
        Assert.Equal([makeups[0].Id, makeups[1].Id], makeupLots.Select(l => l.ProductionOrderId!.Value));
        Assert.All(makeupLots, l => Assert.Equal(main.Number, l.BatchNumber));
    }

    [Fact]
    public async Task Panel_batch_becomes_a_sales_order_with_one_line_per_set_or_part()
    {
        await using var scope = _database.NewScope();
        var orders = NewOrders(scope);

        // 三種都做：一行整組（示範規格 P30-RD12）。
        var set = await orders.QuickOrderAsync(20, null, null, null, default);
        Assert.Equal(ProductParts.All, set.Select(l => l.PartCode));
        Assert.All(set, l => Assert.Equal((20, "P30-RD12"), (l.Quantity, l.SpecCode)));
        _database.Time.Advance(TimeSpan.FromSeconds(1));

        // 只做中模與上沖：每種一行，依一組的順序（上沖在前）。
        var parts = await orders.QuickOrderAsync(10, 24, [ProductParts.Die, ProductParts.UpperPunch], null, default);
        Assert.Equal([ProductParts.UpperPunch, ProductParts.Die], parts.Select(l => l.PartCode));

        var list = await orders.ListAsync(10, default);
        Assert.Equal(2, list.Count);
        Assert.All(list, o => Assert.Equal(OrderService.PanelCustomer, o.Customer));
        Assert.Equal(["P24-SQ08-U", "P24-SQ08-D"], list[0].Lines.Select(l => l.ItemCode));
    }

    private OrderService NewOrders(TestDatabase.Scope scope) =>
        new(scope.Db, new InventoryService(scope.Db, _database.Time, NullLogger<InventoryService>.Instance), scope.Production, _database.Time,
            NullLogger<OrderService>.Instance);

    private async Task<(SalesOrderDto Order, bool Created)> CreateAsync(CreateSalesOrderRequest request, string? key = null)
    {
        await using var scope = _database.NewScope();
        return await NewOrders(scope).CreateAsync(request, key, default);
    }

    private async Task<SalesOrderDto> OrderAsync(Guid id)
    {
        await using var scope = _database.NewScope();
        return (await NewOrders(scope).GetAsync(id, default))!;
    }

    private async Task<ProductionOrderDto> ProductionOrderAsync(Guid id)
    {
        await using var scope = _database.NewScope();
        return (await NewOrders(scope).GetProductionOrderAsync(id, default))!;
    }

    private async Task ArriveAtInspectionAsync(Guid orderId, string spec, string part)
    {
        int qcStep;
        await using (var db = _database.NewContext())
        {
            qcStep = (await db.ProcessSteps.SingleAsync(s => s.IsInspection)).Sequence;
        }

        await ArriveAsync(orderId, spec, part, "qc", qcStep);
    }

    /// <summary>直接把一框屬於 <paramref name="orderId"/> 的料（1 支）放到 <paramref name="station"/> 排隊。</summary>
    private async Task<Guid> ArriveAsync(Guid orderId, string spec, string part, string station, int step)
    {
        await using var scope = _database.NewScope();
        var now = _database.Time.GetUtcNow();
        var batch = await scope.Db.ProductionOrders.Where(o => o.Id == orderId).Select(o => o.ParentId).SingleAsync() is { } parent
            ? await scope.Db.ProductionOrders.Where(o => o.Id == parent).Select(o => o.Number).SingleAsync()
            : await scope.Db.ProductionOrders.Where(o => o.Id == orderId).Select(o => o.Number).SingleAsync();
        var index = await scope.Db.Lots.CountAsync(l => l.BatchNumber == batch) + 1;
        var lot = MaterialLot.Receive(part, 1, station, step, batch, 90 + index, 99, 30, now, spec, productionOrderId: orderId);
        lot.PlaceAt(station, now);
        scope.Db.Lots.Add(lot);
        scope.Production.OnLotArrived(lot, now);
        await scope.Db.SaveChangesAsync();
        _database.Time.Advance(TimeSpan.FromMilliseconds(100));
        return lot.Id;
    }

    private async Task AdvanceAsync(double seconds)
    {
        _database.Time.Advance(TimeSpan.FromSeconds(seconds));
        await using var scope = _database.NewScope();
        await scope.Production.AdvanceOperationsAsync(default);
    }
}
