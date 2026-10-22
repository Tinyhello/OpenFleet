using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenFleet.Api.Data;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Inventory;

/// <summary>
/// 圓棒與備料的庫存、保留、異動帳，以及採購單（Scoped：跟著一個 DbContext）。
/// 修改庫存的方法分兩層：
/// 建構塊（<see cref="ReserveAsync"/>、<see cref="ConsumeAsync"/> 等）只改追蹤中的資料、不存檔，讓呼叫端把好幾件事放進同一筆交易；
/// 對外的操作用 <see cref="RunAsync{T}"/> 包起來，遇到別人同時改了同一列（xmin 不符）就整個重來。
/// </summary>
public sealed class InventoryService(OpenFleetDbContext db, TimeProvider time, ILogger<InventoryService> logger)
{
    /// <summary>圓棒與備料都放原料倉（多出來的備料放回原料倉，是 Claude 選的，待使用者確認）。</summary>
    public const string StoreStation = "raw-store";

    public const int MaxQuantity = 10_000;
    private const int MaxAttempts = 3;

    /// <summary>
    /// 執行一個工作單元並存檔；遇到並行衝突時清掉追蹤中的資料、重新讀取再做一次。
    /// <paramref name="work"/> 必須從資料庫重新讀取它要改的資料，不能沿用上一輪的物件。
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var result = await work();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return result;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                logger.LogInformation("Inventory update conflicted with another writer; retrying ({Attempt}/{Max}).", attempt, MaxAttempts);
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // 兩邊同時替同一個品項建立第一筆庫存列。
                logger.LogInformation("Stock row was created concurrently; retrying ({Attempt}/{Max}).", attempt, MaxAttempts);
            }
        }
    }

    // ---- 建構塊：只改追蹤中的資料，不存檔 ----

    /// <summary>一個品項在一個站點的庫存列（成品倉再依客戶 <paramref name="owner"/> 分開）；還沒有就建立一列。</summary>
    public async Task<StockBalance> GetOrCreateBalanceAsync(
        string itemCode, string stationCode, CancellationToken cancellationToken, string? owner = null)
    {
        var balance = db.StockBalances.Local.SingleOrDefault(b => b.ItemCode == itemCode && b.StationCode == stationCode && b.Owner == owner)
            ?? await db.StockBalances.SingleOrDefaultAsync(
                b => b.ItemCode == itemCode && b.StationCode == stationCode && b.Owner == owner, cancellationToken);
        if (balance is null)
        {
            balance = StockBalance.Create(itemCode, stationCode, time.GetUtcNow(), owner);
            db.StockBalances.Add(balance);
        }

        return balance;
    }

    /// <summary>入庫（收貨、切料產出、多出來的零件、初始庫存），同時記一筆異動。</summary>
    public async Task<StockBalance> AddStockAsync(
        string itemCode, string stationCode, int quantity, StockMovementReason reason, string? reference, CancellationToken cancellationToken,
        string? owner = null)
    {
        var now = time.GetUtcNow();
        var balance = await GetOrCreateBalanceAsync(itemCode, stationCode, cancellationToken, owner);
        balance.Add(quantity, now);
        Record(balance, quantity, reason, reference, now);
        return balance;
    }

    /// <summary>
    /// 保留最多 <paramref name="wanted"/> 個：可用的不夠就保留可用的全部（使用者 2026-10-22：不等料，有多少先用多少）。
    /// 一個都沒有時回傳 null。
    /// </summary>
    public async Task<StockReservation?> ReserveAsync(
        string itemCode, string stationCode, int wanted, string reference, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var balance = await GetOrCreateBalanceAsync(itemCode, stationCode, cancellationToken);
        var taken = balance.Reserve(wanted, now);
        if (taken == 0)
        {
            return null;
        }

        var reservation = StockReservation.Create(balance, taken, reference, now);
        db.StockReservations.Add(reservation);
        return reservation;
    }

    /// <summary>領用保留的一部分或全部：在庫與保留一起扣，記一筆領用。</summary>
    public async Task ConsumeAsync(StockReservation reservation, int quantity, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var balance = await db.StockBalances.SingleAsync(b => b.Id == reservation.StockBalanceId, cancellationToken);
        reservation.Take(quantity, now);
        balance.Consume(quantity, now);
        Record(balance, -quantity, StockMovementReason.Consumption, reservation.Reference, now);
    }

    /// <summary>取消保留還沒用掉的部分。</summary>
    public async Task ReleaseAsync(StockReservation reservation, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var balance = await db.StockBalances.SingleAsync(b => b.Id == reservation.StockBalanceId, cancellationToken);
        balance.Release(reservation.Remaining, now);
        reservation.Take(reservation.Remaining, now);
    }

    private void Record(StockBalance balance, int quantity, StockMovementReason reason, string? reference, DateTimeOffset now) =>
        db.StockMovements.Add(new StockMovement
        {
            ItemCode = balance.ItemCode,
            StationCode = balance.StationCode,
            Quantity = quantity,
            Reason = reason,
            Reference = reference,
            OnHandAfter = balance.OnHand,
            At = now,
        });

    // ---- 對外的操作 ----

    /// <summary>保留並存檔；給還沒有工單的情境（與測試）用。</summary>
    public Task<int> ReserveAvailableAsync(string itemCode, int wanted, string reference, CancellationToken cancellationToken) =>
        RunAsync(async () => (await ReserveAsync(itemCode, StoreStation, wanted, reference, cancellationToken))?.Quantity ?? 0, cancellationToken);

    public async Task<InventorySummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var balances = await db.StockBalances.AsNoTracking().ToListAsync(cancellationToken);
        var stocked = balances.Select(b => b.ItemCode).ToHashSet();
        // 圓棒就算還沒有庫存列也要列出來（才看得到低於安全庫存）。
        var items = await db.Items.AsNoTracking()
            .Where(i => stocked.Contains(i.Code) || i.Kind == ItemKind.Bar)
            .ToDictionaryAsync(i => i.Code, cancellationToken);
        var onOrder = await OnOrderAsync(cancellationToken);

        var rows = balances.Select(b => (Item: items[b.ItemCode], b.StationCode, b.Owner, b.OnHand, b.Reserved)).ToList();
        rows.AddRange(items.Values.Where(i => !stocked.Contains(i.Code))
            .Select(i => (Item: i, StationCode: StoreStation, Owner: (string?)null, OnHand: 0, Reserved: 0)));

        var available = rows.GroupBy(r => r.Item.Code).ToDictionary(g => g.Key, g => g.Sum(r => r.OnHand - r.Reserved));
        var dto = new InventorySummaryDto
        {
            Balances = rows
                .OrderBy(r => r.Item.Kind == ItemKind.Bar ? 0 : r.Item.Kind == ItemKind.Blank ? 1 : 2)
                .ThenBy(r => r.Owner)
                .ThenBy(r => r.Item.PunchSpecCode)
                .ThenBy(r => r.Item.PartCode is null ? -1 : Array.IndexOf(ProductParts.All, r.Item.PartCode))
                .Select(r => new StockBalanceDto
                {
                    ItemCode = r.Item.Code,
                    ItemName = r.Item.Name,
                    Kind = r.Item.Kind,
                    StationCode = r.StationCode,
                    Owner = r.Owner,
                    OnHand = r.OnHand,
                    Reserved = r.Reserved,
                    Available = r.OnHand - r.Reserved,
                    SafetyStock = r.Item.SafetyStock,
                    OnOrder = onOrder.GetValueOrDefault(r.Item.Code),
                    BelowSafetyStock = r.Item.SafetyStock is { } safety && available[r.Item.Code] < safety,
                }).ToList(),
        };

        // 提醒以品項為單位（所有站點加總）。可用量低於安全庫存就提醒，訂了還沒到的不算可用，但會扣在建議量裡。
        dto.Alerts = items.Values
            .Where(i => i.SafetyStock is { } safety && available.GetValueOrDefault(i.Code) < safety)
            .Select(i =>
            {
                var have = available.GetValueOrDefault(i.Code);
                var ordered = onOrder.GetValueOrDefault(i.Code);
                return new StockAlertDto
                {
                    ItemCode = i.Code,
                    ItemName = i.Name,
                    Available = have,
                    SafetyStock = i.SafetyStock!.Value,
                    OnOrder = ordered,
                    SuggestedQuantity = Math.Max(0, i.SafetyStock!.Value - have - ordered),
                };
            }).ToList();
        return dto;
    }

    public async Task<List<StockMovementDto>> GetMovementsAsync(string? itemCode, int take, CancellationToken cancellationToken)
    {
        var query = db.StockMovements.AsNoTracking();
        if (!string.IsNullOrEmpty(itemCode))
        {
            query = query.Where(m => m.ItemCode == itemCode);
        }

        return await query.OrderByDescending(m => m.Id).Take(Math.Clamp(take, 1, 500)).Select(m => new StockMovementDto
        {
            Id = m.Id,
            ItemCode = m.ItemCode,
            StationCode = m.StationCode,
            Quantity = m.Quantity,
            Reason = m.Reason,
            Reference = m.Reference,
            OnHandAfter = m.OnHandAfter,
            At = m.At,
        }).ToListAsync(cancellationToken);
    }

    /// <summary>盤點調整：數量可正可負，不能調到比已保留的還少。</summary>
    public async Task<StockBalanceDto> AdjustAsync(StockAdjustmentRequest request, CancellationToken cancellationToken)
    {
        if (request.Quantity == 0 || Math.Abs(request.Quantity) > MaxQuantity)
        {
            throw new RequestValidationException("quantity", $"Quantity must be non-zero and at most {MaxQuantity} either way.");
        }

        var item = await StockedItemAsync(request.ItemCode, "itemCode", cancellationToken);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()[..Math.Min(request.Note.Trim().Length, 64)];

        var balance = await RunAsync(async () =>
        {
            var now = time.GetUtcNow();
            var balance = await GetOrCreateBalanceAsync(item.Code, StoreStation, cancellationToken);
            if (balance.OnHand + request.Quantity < balance.Reserved)
            {
                throw new RequestValidationException("quantity", $"Stock cannot go below the reserved {balance.Reserved}.");
            }

            balance.Adjust(request.Quantity, now);
            Record(balance, request.Quantity, StockMovementReason.Adjustment, note, now);
            return balance;
        }, cancellationToken);

        logger.LogInformation("Stock of {Item} adjusted by {Quantity} to {OnHand}.", item.Code, request.Quantity, balance.OnHand);
        return (await GetSummaryAsync(cancellationToken)).Balances.Single(b => b.ItemCode == item.Code && b.StationCode == StoreStation && b.Owner is null);
    }

    /// <summary>
    /// 建立採購單（只買圓棒）。帶了 Idempotency-Key 時，同一個鍵重送回傳同一張單（Created＝false）；
    /// 同一個鍵但內容不同是錯誤（409）。兩個同鍵的請求同時到，由唯一索引擋下後到的那一個。
    /// </summary>
    public async Task<(PurchaseOrderDto Order, bool Created)> CreatePurchaseOrderAsync(
        CreatePurchaseOrderRequest request, string? requestKey, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(requestKey) ? null : requestKey.Trim();
        if (key is { Length: > 64 })
        {
            throw new RequestValidationException("Idempotency-Key", "Idempotency-Key must be at most 64 characters.");
        }

        if (key is not null && await FindByKeyAsync(key, cancellationToken) is { } existing)
        {
            return (Replay(existing, request), false);
        }

        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Code == request.ItemCode, cancellationToken);
        if (item is not { Kind: ItemKind.Bar })
        {
            throw new RequestValidationException("itemCode", "Only raw bars can be purchased.");
        }

        if (request.Quantity is < 1 or > MaxQuantity)
        {
            throw new RequestValidationException("quantity", $"Quantity must be between 1 and {MaxQuantity}.");
        }

        var supplier = string.IsNullOrWhiteSpace(request.Supplier) ? null : request.Supplier.Trim();
        if (supplier is { Length: > 64 })
        {
            throw new RequestValidationException("supplier", "Supplier must be at most 64 characters.");
        }

        var order = PurchaseOrder.Create(item.Code, request.Quantity, supplier, key, time.GetUtcNow());
        db.PurchaseOrders.Add(order);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (key is not null && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return (Replay((await FindByKeyAsync(key, cancellationToken))!, request), false);
        }

        logger.LogInformation("Purchase order {Number} created: {Quantity} x {Item}.", order.Number, order.Quantity, order.ItemCode);
        return (ToDto(order), true);
    }

    public async Task<List<PurchaseOrderDto>> GetPurchaseOrdersAsync(PurchaseOrderStatus? status, CancellationToken cancellationToken)
    {
        var query = db.PurchaseOrders.AsNoTracking();
        if (status is { } s)
        {
            query = query.Where(o => o.Status == s);
        }

        return (await query.OrderByDescending(o => o.CreatedAt).Take(200).ToListAsync(cancellationToken)).Select(ToDto).ToList();
    }

    public async Task<PurchaseOrderDto?> GetPurchaseOrderAsync(Guid id, CancellationToken cancellationToken) =>
        await db.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken) is { } order ? ToDto(order) : null;

    /// <summary>
    /// 收貨：採購單改成已收貨，整張的數量入原料倉，同一筆交易。
    /// 同一張單被收兩次（或兩個人同時按）時，只有一次會成功，另一次回 409。
    /// </summary>
    public async Task<PurchaseOrderDto> ReceivePurchaseOrderAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await RunAsync(async () =>
        {
            var order = await LoadOrderedAsync(id, "received", cancellationToken);
            order.Receive(time.GetUtcNow());
            await AddStockAsync(order.ItemCode, StoreStation, order.Quantity, StockMovementReason.Receipt, order.Number, cancellationToken);
            return order;
        }, cancellationToken);

        logger.LogInformation("Purchase order {Number} received: {Quantity} x {Item} into {Station}.", order.Number, order.Quantity, order.ItemCode, StoreStation);
        return ToDto(order);
    }

    public async Task<PurchaseOrderDto> CancelPurchaseOrderAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await RunAsync(async () =>
        {
            var order = await LoadOrderedAsync(id, "cancelled", cancellationToken);
            order.Cancel(time.GetUtcNow());
            return order;
        }, cancellationToken);

        logger.LogInformation("Purchase order {Number} cancelled.", order.Number);
        return ToDto(order);
    }

    private async Task<PurchaseOrder> LoadOrderedAsync(Guid id, string action, CancellationToken cancellationToken)
    {
        var order = await db.PurchaseOrders.SingleOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Purchase order {id} not found.");
        if (order.Status != PurchaseOrderStatus.Ordered)
        {
            throw new ConflictException($"Purchase order {order.Number} is {order.Status.ToString().ToLowerInvariant()} and cannot be {action}.");
        }

        return order;
    }

    private async Task<Item> StockedItemAsync(string? itemCode, string field, CancellationToken cancellationToken)
    {
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Code == itemCode, cancellationToken);
        if (item is not { Kind: ItemKind.Bar or ItemKind.Blank })
        {
            throw new RequestValidationException(field, "Only raw bars and blanks are kept in stock.");
        }

        return item;
    }

    private async Task<Dictionary<string, int>> OnOrderAsync(CancellationToken cancellationToken) =>
        await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Status == PurchaseOrderStatus.Ordered)
            .GroupBy(o => o.ItemCode)
            .ToDictionaryAsync(g => g.Key, g => g.Sum(o => o.Quantity), cancellationToken);

    private Task<PurchaseOrder?> FindByKeyAsync(string key, CancellationToken cancellationToken) =>
        db.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(o => o.RequestKey == key, cancellationToken);

    private static PurchaseOrderDto Replay(PurchaseOrder existing, CreatePurchaseOrderRequest request)
    {
        var supplier = string.IsNullOrWhiteSpace(request.Supplier) ? null : request.Supplier.Trim();
        if (existing.ItemCode != request.ItemCode || existing.Quantity != request.Quantity || existing.Supplier != supplier)
        {
            throw new ConflictException($"Idempotency-Key was already used for purchase order {existing.Number} with different content.");
        }

        return ToDto(existing);
    }

    private static PurchaseOrderDto ToDto(PurchaseOrder o) => new()
    {
        Id = o.Id,
        Number = o.Number,
        ItemCode = o.ItemCode,
        Quantity = o.Quantity,
        Supplier = o.Supplier,
        Status = o.Status,
        CreatedAt = o.CreatedAt,
        ReceivedAt = o.ReceivedAt,
        CancelledAt = o.CancelledAt,
    };
}
