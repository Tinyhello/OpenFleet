using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFleet.Api;
using OpenFleet.Api.Data;
using OpenFleet.Api.Inventory;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;

namespace OpenFleet.Tests;

/// <summary>
/// P5 第 2 步：圓棒與備料的庫存、保留、異動帳、安全庫存提醒，以及由人建立的採購單。
/// 示範的初始庫存：圓棒 40 根（安全庫存 30）、P30-RD12 三種備料各 40 支。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InventoryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Bar = "BAR-D40";
    private TestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await TestDatabase.CreateAsync(postgres, plentyOfBlanks: false);

    public Task DisposeAsync() => Task.CompletedTask;

    private InventoryService NewInventory(OpenFleetDbContext db) => new(db, _database.Time, NullLogger<InventoryService>.Instance);

    private async Task<StockBalanceDto> BalanceAsync(string itemCode)
    {
        await using var db = _database.NewContext();
        return (await NewInventory(db).GetSummaryAsync(default)).Balances.Single(b => b.ItemCode == itemCode);
    }

    private async Task<List<StockAlertDto>> AlertsAsync()
    {
        await using var db = _database.NewContext();
        return (await NewInventory(db).GetSummaryAsync(default)).Alerts;
    }

    [Fact]
    public async Task Demo_stock_is_seeded_and_bars_are_not_below_safety_stock()
    {
        var bar = await BalanceAsync(Bar);
        Assert.Equal((40, 0, 40, 30, false), (bar.OnHand, bar.Reserved, bar.Available, bar.SafetyStock, bar.BelowSafetyStock));
        Assert.Equal(40, (await BalanceAsync("P30-RD12-D-BLK")).OnHand);
        Assert.Null((await BalanceAsync("P30-RD12-D-BLK")).SafetyStock);
        Assert.Empty(await AlertsAsync());
    }

    [Fact]
    public async Task Reserving_more_than_available_takes_what_is_there_and_raises_the_alert()
    {
        await using (var db = _database.NewContext())
        {
            // 使用者 2026-10-22：不等料，有多少先用多少。
            Assert.Equal(40, await NewInventory(db).ReserveAvailableAsync(Bar, 50, "WO-TEST", default));
            Assert.Equal(0, await NewInventory(db).ReserveAvailableAsync(Bar, 5, "WO-OTHER", default));
        }

        var bar = await BalanceAsync(Bar);
        Assert.Equal((40, 40, 0, true), (bar.OnHand, bar.Reserved, bar.Available, bar.BelowSafetyStock));

        // 只提醒、不自動下單；建議量是補到安全庫存還差多少。
        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal((Bar, 0, 30, 0, 30), (alert.ItemCode, alert.Available, alert.SafetyStock, alert.OnOrder, alert.SuggestedQuantity));
        await using var read = _database.NewContext();
        Assert.Empty(await read.PurchaseOrders.ToListAsync());
    }

    [Fact]
    public async Task Two_reservations_at_once_never_reserve_more_than_is_on_hand()
    {
        async Task<int> ReserveAsync(string reference)
        {
            await using var db = _database.NewContext();
            return await NewInventory(db).ReserveAvailableAsync(Bar, 30, reference, default);
        }

        var taken = await Task.WhenAll(ReserveAsync("WO-A"), ReserveAsync("WO-B"));

        // 一邊拿到 30，另一邊重試後只剩 10。
        Assert.Equal([10, 30], taken.Order());
        Assert.Equal((40, 40), ((await BalanceAsync(Bar)).OnHand, (await BalanceAsync(Bar)).Reserved));
        await using var db = _database.NewContext();
        Assert.Equal(40, await db.StockReservations.SumAsync(r => r.Remaining));
    }

    [Fact]
    public async Task Consuming_and_releasing_a_reservation_updates_stock_and_the_ledger()
    {
        await using (var db = _database.NewContext())
        {
            var inventory = NewInventory(db);
            await inventory.RunAsync(async () =>
            {
                var reservation = (await inventory.ReserveAsync(Bar, InventoryService.StoreStation, 20, "WO-1", default))!;
                await inventory.ConsumeAsync(reservation, 15, default);
                return reservation;
            }, default);
        }

        var bar = await BalanceAsync(Bar);
        Assert.Equal((25, 5), (bar.OnHand, bar.Reserved));

        await using (var db = _database.NewContext())
        {
            var inventory = NewInventory(db);
            await inventory.RunAsync(async () =>
            {
                var reservation = await db.StockReservations.SingleAsync(r => r.Reference == "WO-1");
                await inventory.ReleaseAsync(reservation, default);
                return reservation;
            }, default);
        }

        bar = await BalanceAsync(Bar);
        Assert.Equal((25, 0, 25), (bar.OnHand, bar.Reserved, bar.Available));

        await using var read = _database.NewContext();
        var reservationRow = await read.StockReservations.SingleAsync();
        Assert.Equal((0, true), (reservationRow.Remaining, reservationRow.ClosedAt is not null));
        var movements = await NewInventory(read).GetMovementsAsync(Bar, 10, default);
        Assert.Equal(
            [(StockMovementReason.Consumption, -15, 25, "WO-1"), (StockMovementReason.Seed, 40, 40, (string?)null)],
            movements.Select(m => (m.Reason, m.Quantity, m.OnHandAfter, m.Reference)));
    }

    [Fact]
    public async Task Adjustment_cannot_go_below_reserved_and_only_applies_to_bars_and_blanks()
    {
        await using var db = _database.NewContext();
        var inventory = NewInventory(db);
        await inventory.ReserveAvailableAsync(Bar, 25, "WO-1", default);

        Assert.Equal(50, (await inventory.AdjustAsync(new StockAdjustmentRequest { ItemCode = Bar, Quantity = 10, Note = "盤點" }, default)).OnHand);
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            inventory.AdjustAsync(new StockAdjustmentRequest { ItemCode = Bar, Quantity = -30 }, default));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            inventory.AdjustAsync(new StockAdjustmentRequest { ItemCode = Bar, Quantity = 0 }, default));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            inventory.AdjustAsync(new StockAdjustmentRequest { ItemCode = "P30-HX10", Quantity = 5 }, default));

        // 還沒有庫存列的備料也可以盤點進來。
        Assert.Equal(12, (await inventory.AdjustAsync(new StockAdjustmentRequest { ItemCode = "P24-SQ08-U-BLK", Quantity = 12 }, default)).OnHand);
    }

    [Fact]
    public async Task Purchase_order_counts_as_on_order_until_received_then_goes_into_stock_once()
    {
        await using (var db = _database.NewContext())
        {
            await NewInventory(db).ReserveAvailableAsync(Bar, 25, "WO-1", default);
        }

        PurchaseOrderDto order;
        await using (var db = _database.NewContext())
        {
            (order, var created) = await NewInventory(db).CreatePurchaseOrderAsync(
                new CreatePurchaseOrderRequest { ItemCode = Bar, Quantity = 20, Supplier = "示範鋼材" }, null, default);
            Assert.True(created);
            Assert.Equal(PurchaseOrderStatus.Ordered, order.Status);
        }

        // 還沒到的不算可用，但會扣在建議量裡：可用 15、安全 30、已訂 20 → 建議 0。
        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal((15, 20, 0), (alert.Available, alert.OnOrder, alert.SuggestedQuantity));

        // 兩個人同時按收貨，只會入庫一次。
        async Task<bool> ReceiveAsync()
        {
            await using var db = _database.NewContext();
            try
            {
                await NewInventory(db).ReceivePurchaseOrderAsync(order.Id, default);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        Assert.Single(await Task.WhenAll(ReceiveAsync(), ReceiveAsync()), received => received);

        var bar = await BalanceAsync(Bar);
        Assert.Equal((60, 25, 35, 0, false), (bar.OnHand, bar.Reserved, bar.Available, bar.OnOrder, bar.BelowSafetyStock));
        await using var read = _database.NewContext();
        var inventory = NewInventory(read);
        var receipt = (await inventory.GetMovementsAsync(Bar, 1, default)).Single();
        Assert.Equal((StockMovementReason.Receipt, 20, order.Number), (receipt.Reason, receipt.Quantity, receipt.Reference));
        await Assert.ThrowsAsync<ConflictException>(() => inventory.CancelPurchaseOrderAsync(order.Id, default));
    }

    [Fact]
    public async Task Purchase_orders_are_for_bars_only_and_can_be_cancelled_before_receipt()
    {
        await using var db = _database.NewContext();
        var inventory = NewInventory(db);

        await Assert.ThrowsAsync<RequestValidationException>(() => inventory.CreatePurchaseOrderAsync(
            new CreatePurchaseOrderRequest { ItemCode = "P30-RD12-U-BLK", Quantity = 10 }, null, default));
        await Assert.ThrowsAsync<RequestValidationException>(() => inventory.CreatePurchaseOrderAsync(
            new CreatePurchaseOrderRequest { ItemCode = Bar, Quantity = 0 }, null, default));

        var (order, _) = await inventory.CreatePurchaseOrderAsync(new CreatePurchaseOrderRequest { ItemCode = Bar, Quantity = 10 }, null, default);
        Assert.Equal(PurchaseOrderStatus.Cancelled, (await inventory.CancelPurchaseOrderAsync(order.Id, default)).Status);
        await Assert.ThrowsAsync<ConflictException>(() => inventory.ReceivePurchaseOrderAsync(order.Id, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => inventory.ReceivePurchaseOrderAsync(Guid.NewGuid(), default));
        Assert.Equal(40, (await BalanceAsync(Bar)).OnHand);
    }

    [Fact]
    public async Task Resending_with_the_same_idempotency_key_returns_the_same_purchase_order()
    {
        var request = new CreatePurchaseOrderRequest { ItemCode = Bar, Quantity = 30 };

        async Task<(PurchaseOrderDto Order, bool Created)> CreateAsync(CreatePurchaseOrderRequest r)
        {
            await using var db = _database.NewContext();
            return await NewInventory(db).CreatePurchaseOrderAsync(r, "key-1", default);
        }

        // 兩個同鍵的請求同時到，也只有一張。
        var results = await Task.WhenAll(CreateAsync(request), CreateAsync(request));
        Assert.Single(results, r => r.Created);
        Assert.Equal(results[0].Order.Id, results[1].Order.Id);

        var replay = await CreateAsync(request);
        Assert.Equal((results[0].Order.Id, false), (replay.Order.Id, replay.Created));

        await Assert.ThrowsAsync<ConflictException>(() => CreateAsync(new CreatePurchaseOrderRequest { ItemCode = Bar, Quantity = 31 }));

        await using var read = _database.NewContext();
        Assert.Equal(1, await read.PurchaseOrders.CountAsync());
    }
}
