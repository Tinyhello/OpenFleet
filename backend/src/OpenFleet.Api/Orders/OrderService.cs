using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Catalog;
using OpenFleet.Api.Data;
using OpenFleet.Api.Inventory;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Orders;

/// <summary>
/// 客戶訂單與生產工單（Scoped：跟著一個 DbContext）。
/// 建立訂單時每一行明細開一張主工單，記下當時解析好的規格與 BOM 版本，並在原料倉建立它的框；
/// 訂單、明細、工單、框、庫存保留與搬運任務在同一筆交易寫入（庫存被別人同時改動時整個重來）。
/// 報廢後的補做工單由生產端在品管時開立（<see cref="ProductionService"/>）。
/// </summary>
public sealed class OrderService(
    OpenFleetDbContext db, InventoryService inventory, ProductionService production, TimeProvider time, ILogger<OrderService> logger)
{
    public const int MaxLines = 20;

    /// <summary>面板「建立批次」改成下一張客戶訂單時用的客戶名稱。</summary>
    public const string PanelCustomer = "面板下單";

    /// <summary>
    /// 建立客戶訂單。帶了 Idempotency-Key 時，同一個鍵重送回傳同一張訂單（Created＝false），不會多開工單或多建料框；
    /// 同一個鍵但內容不同是錯誤（409）。兩個同鍵的請求同時到，由唯一索引擋下後到的那一個，它重來時會找到先建立的那一張。
    /// </summary>
    public async Task<(SalesOrderDto Order, bool Created)> CreateAsync(
        CreateSalesOrderRequest request, string? requestKey, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(requestKey) ? null : requestKey.Trim();
        if (key is { Length: > 64 })
        {
            throw new RequestValidationException("Idempotency-Key", "Idempotency-Key must be at most 64 characters.");
        }

        var customer = (request.Customer ?? string.Empty).Trim();
        if (customer.Length is 0 or > 64)
        {
            throw new RequestValidationException("customer", "Customer is required and must be at most 64 characters.");
        }

        DateOnly? dueDate = request.DueDate is { } due ? DateOnly.FromDateTime(due) : null;
        if (dueDate < DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime))
        {
            throw new RequestValidationException("dueDate", "Due date cannot be in the past.");
        }

        var lines = (request.Lines ?? [])
            .Select(l => (ItemCode: (l.ItemCode ?? string.Empty).Trim().ToUpperInvariant(), l.Quantity))
            .ToList();
        if (lines.Count is 0 or > MaxLines)
        {
            throw new RequestValidationException("lines", $"An order needs between 1 and {MaxLines} lines.");
        }

        if (key is not null && await FindByKeyAsync(key, cancellationToken) is { } existing)
        {
            EnsureSameContent(existing, customer, dueDate, lines);
            return ((await GetAsync(existing.Id, cancellationToken))!, false);
        }

        var plans = await PlanLinesAsync(lines, cancellationToken);

        var (orderId, created) = await inventory.RunAsync(async () =>
        {
            if (key is not null && await FindByKeyAsync(key, cancellationToken) is { } raced)
            {
                return (raced.Id, false);
            }

            var now = time.GetUtcNow();
            var order = SalesOrder.Create(customer, dueDate, key, now);
            var opened = plans.Select(plan => (Plan: plan, Line: order.AddLine(plan.Item.Code, plan.Quantity))).ToList();
            db.SalesOrders.Add(order);
            foreach (var (plan, line) in opened)
            {
                var workOrder = ProductionOrder.OpenMain(order, line, plan.Item, plan.BomVersion, plan.Spec.Copy(), now);
                db.ProductionOrders.Add(workOrder);
                await production.ReleaseAsync(workOrder, cancellationToken);
            }

            return (order.Id, true);
        }, cancellationToken);

        var dto = (await GetAsync(orderId, cancellationToken))!;
        if (!created)
        {
            var stored = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == orderId, cancellationToken);
            EnsureSameContent(stored, customer, dueDate, lines);
            return (dto, false);
        }

        logger.LogInformation("Sales order {Number} for {Customer}: {Lines} line(s), work orders {WorkOrders}.",
            dto.Number, customer, dto.Lines.Count, string.Join(", ", dto.Lines.SelectMany(l => l.ProductionOrders).Select(o => o.Number)));
        return (dto, true);
    }

    /// <summary>
    /// 面板的「建立批次」：下一張客戶訂單。三種零件都做就是一行整組；只做其中幾種就每種零件一行。
    /// 沒有指定規格時用該桿徑的示範規格。回傳這張訂單建立的每一框（依明細、零件、框號排序）。
    /// </summary>
    public async Task<List<MaterialLot>> QuickOrderAsync(
        int? quantity, int? rodDiameterMm, IReadOnlyList<string>? partCodes, string? specCode, CancellationToken cancellationToken)
    {
        var settings = await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken);
        var total = quantity ?? settings.LotQuantity;
        ProductionService.ValidateQuantity(total, ProductionService.MaxBatchQuantity, "quantity");
        var (spec, _) = await production.ResolveSpecAsync(specCode, rodDiameterMm, cancellationToken);
        if (spec is null)
        {
            throw new RequestValidationException("specCode", "No punch spec is available for this rod diameter.");
        }

        var parts = ProductionService.ValidateParts(partCodes);
        var lines = parts.Length == ProductParts.All.Length
            ? [new CreateSalesOrderLineRequest { ItemCode = spec.SpecCode, Quantity = total }]
            : parts.Select(p => new CreateSalesOrderLineRequest { ItemCode = CatalogService.PartItemCode(spec.SpecCode, p), Quantity = total }).ToList();
        var (order, _) = await CreateAsync(new CreateSalesOrderRequest { Customer = PanelCustomer, Lines = lines }, null, cancellationToken);

        var lineByOrder = order.Lines.SelectMany(l => l.ProductionOrders.Select(o => (o.Id, l.LineNumber))).ToDictionary(x => x.Id, x => x.LineNumber);
        var orderIds = lineByOrder.Keys.ToList();
        var lots = await db.Lots.AsNoTracking()
            .Where(l => l.ProductionOrderId != null && orderIds.Contains(l.ProductionOrderId.Value))
            .ToListAsync(cancellationToken);
        return lots
            .OrderBy(l => lineByOrder[l.ProductionOrderId!.Value])
            .ThenBy(l => Array.IndexOf(ProductParts.All, l.PartCode))
            .ThenBy(l => l.BinIndex)
            .ToList();
    }

    public async Task<SalesOrderDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        return order is null ? null : (await BuildAsync([order], cancellationToken)).Single();
    }

    /// <summary>最近的訂單（新的在前）。</summary>
    public async Task<List<SalesOrderDto>> ListAsync(int take, CancellationToken cancellationToken)
    {
        var orders = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Number)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(cancellationToken);
        return await BuildAsync(orders, cancellationToken);
    }

    /// <summary>一張工單，含它的框（不含出貨箱）與報廢註記。</summary>
    public async Task<ProductionOrderDto?> GetProductionOrderAsync(Guid id, CancellationToken cancellationToken)
    {
        var workOrder = await db.ProductionOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (workOrder is null)
        {
            return null;
        }

        var line = await db.SalesOrderLines.AsNoTracking().SingleAsync(l => l.Id == workOrder.SalesOrderLineId, cancellationToken);
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == line.SalesOrderId, cancellationToken);
        var dto = (await BuildAsync([order], cancellationToken)).Single()
            .Lines.Single(l => l.LineNumber == line.LineNumber)
            .ProductionOrders.Single(o => o.Id == id);
        var lots = await db.Lots.AsNoTracking().Where(l => l.ProductionOrderId == id && !l.IsBox)
            .OrderBy(l => l.PartCode).ThenBy(l => l.LotNumber).ToListAsync(cancellationToken);
        dto.Lots = lots.OrderBy(l => Array.IndexOf(ProductParts.All, l.PartCode)).ThenBy(l => l.LotNumber).Select(ProductionEndpoints.ToDto).ToList();
        return dto;
    }

    /// <summary>最近開立的工單（新的在前），含補做工單。</summary>
    public async Task<List<ProductionOrderDto>> ListProductionOrdersAsync(int take, CancellationToken cancellationToken)
    {
        var lineIds = await db.ProductionOrders.AsNoTracking()
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Number)
            .Take(Math.Clamp(take, 1, 200))
            .Select(o => o.SalesOrderLineId)
            .ToListAsync(cancellationToken);
        var orderIds = await db.SalesOrderLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).Select(l => l.SalesOrderId).Distinct()
            .ToListAsync(cancellationToken);
        var orders = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).Where(o => orderIds.Contains(o.Id)).ToListAsync(cancellationToken);
        return (await BuildAsync(orders, cancellationToken))
            .SelectMany(o => o.Lines).SelectMany(l => l.ProductionOrders)
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Number)
            .Take(Math.Clamp(take, 1, 200))
            .ToList();
    }

    private sealed record LinePlan(Item Item, int Quantity, int BomVersion, ProductionSpec Spec);

    /// <summary>每一行：品項必須是整組或單一零件、數量 1～300；解析規格與這個品項目前的 BOM 版本。</summary>
    private async Task<List<LinePlan>> PlanLinesAsync(List<(string ItemCode, int Quantity)> lines, CancellationToken cancellationToken)
    {
        var codes = lines.Select(l => l.ItemCode).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => codes.Contains(i.Code)).ToDictionaryAsync(i => i.Code, cancellationToken);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!items.TryGetValue(lines[i].ItemCode, out var item) || item.Kind is not (ItemKind.Set or ItemKind.Part))
            {
                throw new RequestValidationException($"lines[{i}].itemCode", $"{lines[i].ItemCode} is not a set or a part that can be ordered.");
            }

            ProductionService.ValidateQuantity(lines[i].Quantity, ProductionService.MaxBatchQuantity, $"lines[{i}].quantity");
        }

        var specCodes = items.Values.Select(i => i.PunchSpecCode!).Distinct().ToList();
        var specs = (await db.PunchSpecs.AsNoTracking().Include(s => s.Rod).Where(s => specCodes.Contains(s.Code)).ToListAsync(cancellationToken))
            .ToDictionary(s => s.Code, s => ProductionSpec.From(s, CatalogService.DieOuterDiameterMm));
        var boms = await db.BomVersions.AsNoTracking().Where(b => codes.Contains(b.ItemCode))
            .GroupBy(b => b.ItemCode).Select(g => new { ItemCode = g.Key, Version = g.Max(b => b.Version) })
            .ToDictionaryAsync(b => b.ItemCode, b => b.Version, cancellationToken);

        return lines.Select(l =>
        {
            var item = items[l.ItemCode];
            return new LinePlan(item, l.Quantity, boms[item.Code], specs[item.PunchSpecCode!]);
        }).ToList();
    }

    private Task<SalesOrder?> FindByKeyAsync(string key, CancellationToken cancellationToken) =>
        db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.RequestKey == key, cancellationToken);

    /// <summary>同一個鍵重送：內容要和第一次一樣，否則是錯誤。</summary>
    private static void EnsureSameContent(SalesOrder existing, string customer, DateOnly? dueDate, List<(string ItemCode, int Quantity)> lines)
    {
        var stored = existing.Lines.OrderBy(l => l.LineNumber).Select(l => (l.ItemCode, l.Quantity)).ToList();
        if (existing.Customer != customer || existing.DueDate != dueDate || !stored.SequenceEqual(lines))
        {
            throw new ConflictException($"Idempotency-Key was already used for sales order {existing.Number} with different content.");
        }
    }

    /// <summary>
    /// 負責人同意這張訂單分批出貨（使用者 2026-10-22）。一張訂單只能同意一次；同意後，下一輪會把已經包好、
    /// 湊成完整的組先裝箱出貨，剩下的要等整張訂單都做完才一起出。
    /// 已經全部做完的訂單本來就會整張出貨，不用分批；還沒有任何完整的組包好時也不能同意（409）。
    /// 兩個人同時按，由 xmin 擋下後到的那一個（409）。
    /// </summary>
    public async Task<SalesOrderDto> ApprovePartialShipmentAsync(Guid id, PartialShipmentRequest request, CancellationToken cancellationToken)
    {
        var approvedBy = (request.ApprovedBy ?? string.Empty).Trim();
        if (approvedBy.Length is 0 or > 64)
        {
            throw new RequestValidationException("approvedBy", "The approver's name is required and must be at most 64 characters.");
        }

        var order = await db.SalesOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Sales order {id} not found.");
        if (order.PartialApprovedAt is not null)
        {
            throw new ConflictException($"A partial shipment of {order.Number} was already approved; the rest ships when the whole order is complete.");
        }

        if (await db.Deliveries.AnyAsync(d => d.SalesOrderId == id, cancellationToken))
        {
            throw new ConflictException($"Sales order {order.Number} is already being shipped.");
        }

        var lineIds = order.Lines.Select(l => l.Id).ToList();
        var workOrders = await db.ProductionOrders.AsNoTracking().Where(o => lineIds.Contains(o.SalesOrderLineId)).ToListAsync(cancellationToken);
        var lineOf = workOrders.ToDictionary(o => o.Id, o => o.SalesOrderLineId);
        var workOrderIds = lineOf.Keys.ToList();
        var lots = await db.Lots.AsNoTracking()
            .Where(l => !l.IsBox && l.ProductionOrderId != null && workOrderIds.Contains(l.ProductionOrderId.Value))
            .Select(l => new { OrderId = l.ProductionOrderId!.Value, l.PartCode, l.Quantity, l.Status, l.CurrentStepSequence })
            .ToListAsync(cancellationToken);
        var mains = workOrders.Where(o => o.Kind == ProductionOrderKind.Main).ToDictionary(o => o.SalesOrderLineId);

        int Packed(SalesOrderLine line, string part) =>
            lots.Where(l => l.Status == LotStatus.Packed && l.PartCode == part && lineOf[l.OrderId] == line.Id).Sum(l => l.Quantity);

        var complete = !lots.Any(l => Fulfillment.IsActive(l.Status, l.CurrentStepSequence))
            && order.Lines.All(line => mains[line.Id].Parts.All(part => Packed(line, part) >= line.Quantity));
        if (complete)
        {
            throw new ConflictException($"Sales order {order.Number} is complete and ships as a whole.");
        }

        if (order.Lines.All(line => Fulfillment.CompleteSets(mains[line.Id].Parts, part => Packed(line, part), line.Quantity) == 0))
        {
            throw new ConflictException($"No complete set of {order.Number} is packed yet.");
        }

        order.ApprovePartial(approvedBy, time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException($"Sales order {order.Number} was changed by someone else; check whether the partial shipment is already approved.");
        }

        logger.LogInformation("Sales order {Number}: partial shipment approved by {ApprovedBy}.", order.Number, approvedBy);
        return (await GetAsync(id, cancellationToken))!;
    }

    /// <summary>
    /// 負責人把訂單設成急單（使用者 2026-10-22：急單排在所有訂單前面）。之後這張訂單的框（含補做工單與出貨箱）
    /// 在各站排隊、開爐裝爐、分配圓棒、派車時都排在一般訂單前面（<see cref="SchedulePriority"/>），下一輪就生效。
    /// 已經是急單、已經全部做完或出貨的訂單回 409；兩個人同時按，由 xmin 擋下後到的那一個（409）。
    /// </summary>
    public async Task<SalesOrderDto> MarkRushAsync(Guid id, RushRequest request, CancellationToken cancellationToken)
    {
        var requestedBy = (request.RequestedBy ?? string.Empty).Trim();
        if (requestedBy.Length is 0 or > 64)
        {
            throw new RequestValidationException("requestedBy", "The name of the person in charge is required and must be at most 64 characters.");
        }

        var order = await db.SalesOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Sales order {id} not found.");
        if (order.IsRush)
        {
            throw new ConflictException($"Sales order {order.Number} is already a rush order.");
        }

        var status = (await BuildAsync([order], cancellationToken)).Single().Status;
        if (status is SalesOrderStatus.Completed or SalesOrderStatus.Shipped)
        {
            throw new ConflictException($"Sales order {order.Number} is already finished; there is nothing left to rush.");
        }

        order.MarkRush(requestedBy, time.GetUtcNow());
        await SaveRushAsync(order, cancellationToken);
        logger.LogInformation("Sales order {Number}: marked as a rush order by {RequestedBy}.", order.Number, requestedBy);
        return (await GetAsync(id, cancellationToken))!;
    }

    /// <summary>取消急單：之後照交期排。不是急單時回 409。</summary>
    public async Task<SalesOrderDto> CancelRushAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.SalesOrders.SingleOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Sales order {id} not found.");
        if (!order.IsRush)
        {
            throw new ConflictException($"Sales order {order.Number} is not a rush order.");
        }

        order.CancelRush();
        await SaveRushAsync(order, cancellationToken);
        logger.LogInformation("Sales order {Number}: rush cancelled.", order.Number);
        return (await GetAsync(id, cancellationToken))!;
    }

    private async Task SaveRushAsync(SalesOrder order, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException($"Sales order {order.Number} was changed by someone else; check whether it is already a rush order.");
        }
    }

    private async Task<List<SalesOrderDto>> BuildAsync(IReadOnlyList<SalesOrder> orders, CancellationToken cancellationToken)
    {
        var lineIds = orders.SelectMany(o => o.Lines).Select(l => l.Id).ToList();
        var workOrders = await db.ProductionOrders.AsNoTracking().Where(o => lineIds.Contains(o.SalesOrderLineId)).ToListAsync(cancellationToken);
        var workOrderIds = workOrders.Select(o => o.Id).ToList();
        var numbers = workOrders.ToDictionary(o => o.Id, o => o.Number);
        var lots = await db.Lots.AsNoTracking()
            .Where(l => !l.IsBox && l.ProductionOrderId != null && workOrderIds.Contains(l.ProductionOrderId.Value))
            .Select(l => new LotState(l.ProductionOrderId!.Value, l.PartCode, l.Quantity, l.Status, l.CurrentStepSequence))
            .ToListAsync(cancellationToken);
        var scraps = await db.ScrapRecords.AsNoTracking().Where(r => workOrderIds.Contains(r.MainOrderId)).OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        var itemCodes = orders.SelectMany(o => o.Lines).Select(l => l.ItemCode).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemCodes.Contains(i.Code)).ToDictionaryAsync(i => i.Code, cancellationToken);

        var orderIds = orders.Select(o => o.Id).ToList();
        var deliveries = await db.Deliveries.AsNoTracking().Include(d => d.Lines).Where(d => orderIds.Contains(d.SalesOrderId))
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Number)
            .ToListAsync(cancellationToken);
        var boxIds = deliveries.SelectMany(d => d.Lines).Select(l => l.BoxLotId).ToList();
        var boxNumbers = await db.Lots.AsNoTracking().Where(l => boxIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.LotNumber, cancellationToken);
        var surplus = await db.SurplusTransfers.AsNoTracking().Where(t => orderIds.Contains(t.SalesOrderId)).ToListAsync(cancellationToken);

        return orders.Select(order =>
        {
            var lineNumbers = order.Lines.ToDictionary(l => l.Id, l => l.LineNumber);
            var lineDtos = order.Lines.OrderBy(l => l.LineNumber).Select(line =>
            {
                var item = items[line.ItemCode];
                var family = workOrders.Where(o => o.SalesOrderLineId == line.Id).ToList();
                var main = family.Single(o => o.Kind == ProductionOrderKind.Main);
                var familyIds = family.Select(o => o.Id).ToHashSet();
                var familyLots = lots.Where(l => familyIds.Contains(l.OrderId)).ToList();
                var parts = main.Parts.Select(part => Progress(
                    part, line.Quantity, main.ExtraQuantity, familyLots,
                    scraps.Where(r => r.MainOrderId == main.Id), family.Where(o => o.Kind == ProductionOrderKind.Makeup),
                    shipped: deliveries.Where(d => d.ShippedAt is not null).SelectMany(d => d.Lines)
                        .Where(l => l.SalesOrderLineId == line.Id && l.PartCode == part).Sum(l => l.Quantity),
                    surplus: surplus.Where(t => t.SalesOrderLineId == line.Id && t.PartCode == part).Sum(t => t.Quantity))).ToList();
                return new SalesOrderLineDto
                {
                    LineNumber = line.LineNumber,
                    ItemCode = line.ItemCode,
                    ItemName = item.Name,
                    Kind = item.Kind,
                    Quantity = line.Quantity,
                    IsComplete = !familyLots.Any(IsActive) && parts.All(p => p.Finished >= p.Ordered),
                    Parts = parts,
                    ProductionOrders = family.OrderBy(o => o.Kind).ThenBy(o => o.CreatedAt).ThenBy(o => o.Number)
                        .Select(o => ToDto(o, order, line, lots, scraps, workOrders, numbers)).ToList(),
                };
            }).ToList();

            return new SalesOrderDto
            {
                Id = order.Id,
                Number = order.Number,
                Customer = order.Customer,
                DueDate = order.DueDate?.ToDateTime(TimeOnly.MinValue),
                CreatedAt = order.CreatedAt,
                Status = StatusOf(lineDtos),
                PartialApprovedBy = order.PartialApprovedBy,
                PartialApprovedAt = order.PartialApprovedAt,
                IsRush = order.IsRush,
                RushRequestedBy = order.RushRequestedBy,
                RushRequestedAt = order.RushRequestedAt,
                Lines = lineDtos,
                Deliveries = deliveries.Where(d => d.SalesOrderId == order.Id).Select(d => new DeliveryDto
                {
                    Number = d.Number,
                    IsPartial = d.IsPartial,
                    CreatedAt = d.CreatedAt,
                    ShippedAt = d.ShippedAt,
                    Lines = d.Lines.OrderBy(l => lineNumbers[l.SalesOrderLineId]).ThenBy(l => Array.IndexOf(ProductParts.All, l.PartCode))
                        .Select(l => new DeliveryLineDto
                        {
                            LineNumber = lineNumbers[l.SalesOrderLineId],
                            PartCode = l.PartCode,
                            Quantity = l.Quantity,
                            BoxLotNumber = boxNumbers.GetValueOrDefault(l.BoxLotId, string.Empty),
                        }).ToList(),
                }).ToList(),
            };
        }).ToList();
    }

    /// <summary>全部出貨了 → 已出貨；全部做完 → 完成（等貨車）；出過一批 → 已分批出貨；其餘是生產中。</summary>
    private static SalesOrderStatus StatusOf(List<SalesOrderLineDto> lines)
    {
        var parts = lines.SelectMany(l => l.Parts).ToList();
        if (parts.All(p => p.Shipped >= p.Ordered))
        {
            return SalesOrderStatus.Shipped;
        }

        if (lines.All(l => l.IsComplete))
        {
            return SalesOrderStatus.Completed;
        }

        return parts.Any(p => p.Shipped > 0) ? SalesOrderStatus.PartiallyShipped : SalesOrderStatus.InProduction;
    }

    private sealed record LotState(Guid OrderId, string PartCode, int Quantity, LotStatus Status, int? Step);

    private static bool IsFinished(LotState lot) => Fulfillment.IsFinished(lot.Status, lot.Step);

    private static bool IsActive(LotState lot) => Fulfillment.IsActive(lot.Status, lot.Step);

    private static PartProgressDto Progress(
        string part, int ordered, int extra, IEnumerable<LotState> lots, IEnumerable<ScrapRecord> scraps, IEnumerable<ProductionOrder> makeups,
        int shipped, int surplus)
    {
        var ofPart = lots.Where(l => l.PartCode == part).ToList();
        return new PartProgressDto
        {
            PartCode = part,
            Ordered = ordered,
            Extra = extra,
            Finished = ofPart.Where(IsFinished).Sum(l => l.Quantity),
            InProgress = ofPart.Where(IsActive).Sum(l => l.Quantity),
            Scrapped = scraps.Where(r => r.PartCode == part).Sum(r => r.Quantity),
            Makeup = makeups.Where(o => o.PartCode == part).Sum(o => o.Quantity),
            Shipped = shipped,
            Surplus = surplus,
        };
    }

    private static ProductionOrderDto ToDto(
        ProductionOrder workOrder, SalesOrder order, SalesOrderLine line, List<LotState> lots, List<ScrapRecord> scraps,
        List<ProductionOrder> workOrders, Dictionary<Guid, string> numbers)
    {
        var own = lots.Where(l => l.OrderId == workOrder.Id).ToList();
        var makeups = workOrders.Where(o => o.ParentId == workOrder.Id);
        var scrappedHere = scraps.Where(r => r.SourceOrderId == workOrder.Id);
        return new ProductionOrderDto
        {
            Id = workOrder.Id,
            Number = workOrder.Number,
            Kind = workOrder.Kind,
            ParentNumber = workOrder.ParentId is { } parent ? numbers[parent] : null,
            SalesOrderNumber = order.Number,
            LineNumber = line.LineNumber,
            ItemCode = workOrder.ItemCode,
            PartCode = workOrder.PartCode,
            Quantity = workOrder.Quantity,
            ExtraQuantity = workOrder.ExtraQuantity,
            BomVersion = workOrder.BomVersion,
            Spec = new ProductionSpecDto
            {
                SpecCode = workOrder.Spec.SpecCode,
                SpecName = workOrder.Spec.SpecName,
                RodDiameterMm = workOrder.Spec.RodDiameterMm,
                PiecesPerBin = workOrder.Spec.PiecesPerBin,
                DieOuterDiameterMm = workOrder.Spec.DieOuterDiameterMm,
                BlankLengthMm = ProductParts.All.ToDictionary(p => p, workOrder.Spec.BlankLengthFor),
                RoughSecondsPerPiece = workOrder.Spec.RoughSecondsPerPiece,
                FinishSecondsPerPiece = workOrder.Spec.FinishSecondsPerPiece,
            },
            CreatedAt = workOrder.CreatedAt,
            IsComplete = own.Count > 0 && !own.Any(IsActive),
            Parts = workOrder.Parts
                .Select(part => Progress(part, workOrder.Quantity, workOrder.ExtraQuantity, own, scrappedHere, makeups, shipped: 0, surplus: 0))
                .ToList(),
            ScrapNotes = scraps.Where(r => r.MainOrderId == workOrder.Id).Select(r => new ScrapNoteDto
            {
                SourceOrderNumber = numbers[r.SourceOrderId],
                LotNumber = r.LotNumber,
                PartCode = r.PartCode,
                Quantity = r.Quantity,
                CoveredByExtra = r.CoveredByExtra,
                MakeupOrderNumber = r.MakeupOrderId is { } makeup ? numbers[makeup] : null,
                At = r.CreatedAt,
            }).ToList(),
        };
    }
}
