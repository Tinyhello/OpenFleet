using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Inventory;
using OpenFleet.Api.Orders;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Production;

/// <summary>
/// 訂單的裝箱與出貨（使用者 2026-10-22）：
/// 整張訂單都做完了才一起出貨，做完就直接出；負責人同意的話可以先出一批（只出完整的組、只能分一次），剩下的等全部到齊再出。
/// 品管 → 包裝 → 出貨；開工時多做、出貨後用剩的零件依規格與客戶收進成品倉，只是存放。
/// 沒有訂單的批次仍照 P4 的規則（同批次同零件包完併成一箱、整批到齊才出貨）。
/// </summary>
public sealed partial class ProductionService
{
    /// <summary>多出來的零件送去的站點（成品倉）。</summary>
    public const string FinishedStoreStation = "finished-store";

    /// <summary>
    /// 有包好的框的訂單，逐張看要不要裝箱：
    /// 負責人同意分批、還沒出過分批的，把已經湊成完整的組先裝箱；
    /// 整張訂單都做完（沒有還在流動的框，每一行每種零件包好的夠出剩下的）就裝最後一次，多出來的裝箱送成品倉。
    /// 箱子在包裝站等待搬運，送到出貨站（或成品倉）。
    /// </summary>
    private async Task<bool> BoxOrdersAsync(ProductionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var orderIds = await (
                from lot in db.Lots
                where lot.Status == LotStatus.Packed && lot.ProductionOrderId != null
                join workOrder in db.ProductionOrders on lot.ProductionOrderId equals workOrder.Id
                join line in db.SalesOrderLines on workOrder.SalesOrderLineId equals line.Id
                select line.SalesOrderId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var boxed = false;
        foreach (var orderId in orderIds)
        {
            boxed |= await BoxOrderAsync(orderId, settings, now, cancellationToken);
        }

        if (boxed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return boxed;
    }

    private async Task<bool> BoxOrderAsync(Guid orderId, ProductionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var order = await db.SalesOrders.Include(o => o.Lines).SingleAsync(o => o.Id == orderId, cancellationToken);
        var deliveries = await db.Deliveries.Include(d => d.Lines).Where(d => d.SalesOrderId == orderId).ToListAsync(cancellationToken);
        if (deliveries.Any(d => !d.IsPartial))
        {
            return false;
        }

        var lineIds = order.Lines.Select(l => l.Id).ToList();
        var workOrders = await db.ProductionOrders.AsNoTracking().Where(o => lineIds.Contains(o.SalesOrderLineId)).ToListAsync(cancellationToken);
        var lineOf = workOrders.ToDictionary(o => o.Id, o => o.SalesOrderLineId);
        var workOrderIds = lineOf.Keys.ToList();
        var mains = workOrders.Where(o => o.Kind == ProductionOrderKind.Main).ToDictionary(o => o.SalesOrderLineId);
        // 還在流動的框只讀狀態、不追蹤：它們同時會被搬運事件修改（例如卸料後開始排隊），追蹤的話，
        // 這一輪後面的步驟（空機台開始下一框）拿到的會是這裡讀到的舊狀態。會被裝箱改動的只有包好、停在包裝站的框，只追蹤這些。
        var states = await db.Lots.AsNoTracking()
            .Where(l => !l.IsBox && l.ProductionOrderId != null && workOrderIds.Contains(l.ProductionOrderId.Value))
            .Select(l => new { l.Status, l.CurrentStepSequence })
            .ToListAsync(cancellationToken);
        var lots = await db.Lots
            .Where(l => !l.IsBox && l.Status == LotStatus.Packed && l.ProductionOrderId != null && workOrderIds.Contains(l.ProductionOrderId.Value))
            .ToListAsync(cancellationToken);

        List<MaterialLot> Packed(SalesOrderLine line, string part) =>
            lots.Where(l => l.Status == LotStatus.Packed && l.PartCode == part && lineOf[l.ProductionOrderId!.Value] == line.Id)
                .OrderBy(l => l.LotNumber).ToList();

        int Delivered(SalesOrderLine line, string part) =>
            deliveries.SelectMany(d => d.Lines).Where(l => l.SalesOrderLineId == line.Id && l.PartCode == part).Sum(l => l.Quantity);

        var changed = false;

        // 負責人同意分批：先出已經湊成完整的組（一張訂單只分一次）。
        if (order.PartialApprovedAt is not null && !deliveries.Any(d => d.IsPartial))
        {
            var plan = order.Lines.OrderBy(l => l.LineNumber)
                .Select(line => (Line: line, Parts: mains[line.Id].Parts,
                    Sets: Fulfillment.CompleteSets(mains[line.Id].Parts, part => Packed(line, part).Sum(l => l.Quantity), line.Quantity)))
                .Where(p => p.Sets > 0)
                .ToList();
            if (plan.Count > 0)
            {
                var partial = Delivery.Create(order, deliveries.Count + 1, isPartial: true, now);
                db.Deliveries.Add(partial);
                foreach (var (line, parts, sets) in plan)
                {
                    foreach (var part in parts)
                    {
                        BoxForDelivery(partial, line, part, Packed(line, part), sets, settings, now);
                    }
                }

                deliveries.Add(partial);
                changed = true;
                logger.LogInformation("Order {Order}: partial delivery {Delivery} boxed ({Sets}), approved by {ApprovedBy}.",
                    order.Number, partial.Number, string.Join(", ", plan.Select(p => $"line {p.Line.LineNumber} x{p.Sets}")), order.PartialApprovedBy);
            }
        }

        // 整張訂單：沒有還在流動的框（含補做與多做的），而且每一行每種零件包好的夠出剩下的。
        if (states.Any(l => Fulfillment.IsActive(l.Status, l.CurrentStepSequence)))
        {
            return changed;
        }

        var remaining = order.Lines.OrderBy(l => l.LineNumber)
            .SelectMany(line => mains[line.Id].Parts.Select(part => (Line: line, Part: part, Need: line.Quantity - Delivered(line, part))))
            .ToList();
        if (remaining.Any(r => Packed(r.Line, r.Part).Sum(l => l.Quantity) < r.Need))
        {
            return changed;
        }

        Delivery? final = null;
        if (remaining.Any(r => r.Need > 0))
        {
            final = Delivery.Create(order, deliveries.Count + 1, isPartial: false, now);
            db.Deliveries.Add(final);
        }

        foreach (var (line, part, need) in remaining)
        {
            if (need > 0)
            {
                BoxForDelivery(final!, line, part, Packed(line, part), need, settings, now);
            }

            // 出完之後還剩的就是多做的用剩的，裝一箱送成品倉。
            var surplus = Packed(line, part);
            if (surplus.Count > 0)
            {
                BoxSurplus(order, line, mains[line.Id], part, surplus, settings, now);
            }
        }

        logger.LogInformation("Order {Order}: complete; {Delivery} boxed for shipping.", order.Number, final?.Number ?? "nothing left to ship");
        return true;
    }

    /// <summary>
    /// 從包好的框裝 <paramref name="quantity"/> 支到出貨箱：整框放得下的整框放進去，最後一框只要一部分時分出來（分批出貨才會發生）。
    /// 箱子在包裝站等待搬運到出貨站。
    /// </summary>
    private void BoxForDelivery(
        Delivery delivery, SalesOrderLine line, string part, List<MaterialLot> packed, int quantity, ProductionSettings settings, DateTimeOffset now)
    {
        var sample = packed[0];
        var left = quantity;
        foreach (var lot in packed)
        {
            if (left == 0)
            {
                break;
            }

            var taken = lot;
            if (lot.Quantity > left)
            {
                taken = MaterialLot.SplitPacked(lot, left, now);
                db.Lots.Add(taken);
            }

            left -= taken.Quantity;
            taken.MoveIntoBox(now);
        }

        var box = MaterialLot.CreateBox(
            $"{delivery.Number}-{line.LineNumber}{ProductParts.Letter(part)}", delivery.Number, sample, quantity, delivery.Id, now);
        db.Lots.Add(box);
        delivery.AddLine(line, part, quantity, box);
        db.InventoryMovements.Add(InventoryMovement.Record(box, LocationCode.Station(box.StationCode!), "Boxed", now));
        db.TransportTasks.Add(TransportTask.Create(box, box.StationCode!, settings.BufferStationCode, TransportTaskKind.WipTransfer, now));
    }

    /// <summary>多出來的零件（開工時多做、出貨後用剩的）裝一箱，送去成品倉；到了才記進成品倉的庫存（依客戶分開）。</summary>
    private void BoxSurplus(
        SalesOrder order, SalesOrderLine line, ProductionOrder main, string part, List<MaterialLot> packed, ProductionSettings settings, DateTimeOffset now)
    {
        foreach (var lot in packed)
        {
            lot.MoveIntoBox(now);
        }

        var box = MaterialLot.CreateBox(
            $"{order.Number}-{line.LineNumber}{ProductParts.Letter(part)}-SUR", order.Number, packed[0], packed.Sum(l => l.Quantity), null, now);
        db.Lots.Add(box);
        db.SurplusTransfers.Add(SurplusTransfer.Create(order, line, Catalog.CatalogService.PartItemCode(main.Spec.SpecCode, part), part, box, now));
        db.InventoryMovements.Add(InventoryMovement.Record(box, LocationCode.Station(box.StationCode!), "Boxed", now));
        db.TransportTasks.Add(TransportTask.Create(box, box.StationCode!, FinishedStoreStation, TransportTaskKind.WipTransfer, now));
        logger.LogInformation("Order {Order} line {Line}: {Quantity} surplus piece(s) of {Part} boxed for the finished-goods store.",
            order.Number, line.LineNumber, box.Quantity, part);
    }

    /// <summary>
    /// 送到成品倉的箱子：記進成品倉的庫存（品項＝規格加零件，依客戶分開）。
    /// 和其他寫入庫存的請求同時改到同一列時，這一輪放棄，下一輪重來。
    /// </summary>
    private async Task<bool> ReceiveSurplusAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transfers = await db.SurplusTransfers.Where(t => t.ReceivedAt == null).ToListAsync(cancellationToken);
        if (transfers.Count == 0)
        {
            return false;
        }

        var boxIds = transfers.Select(t => t.BoxLotId).ToList();
        var arrived = await db.Lots.AsNoTracking()
            .Where(l => boxIds.Contains(l.Id) && l.Status == LotStatus.Staged && l.StationCode == FinishedStoreStation)
            .ToDictionaryAsync(l => l.Id, l => l.LotNumber, cancellationToken);
        if (arrived.Count == 0)
        {
            return false;
        }

        foreach (var transfer in transfers.Where(t => arrived.ContainsKey(t.BoxLotId)))
        {
            await inventory.AddStockAsync(transfer.ItemCode, FinishedStoreStation, transfer.Quantity, StockMovementReason.Surplus,
                arrived[transfer.BoxLotId], cancellationToken, owner: transfer.Customer);
            transfer.Receive(now);
            logger.LogInformation("Finished-goods store: {Quantity} x {Item} for {Customer} received.", transfer.Quantity, transfer.ItemCode, transfer.Customer);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("Finished-goods receipt conflicted with another stock update; retrying next cycle.");
            return false;
        }
    }

    /// <summary>
    /// 貨車到了：每張還沒出貨的出貨單，箱子都在出貨站了就整張載走（標記出貨），回傳這些箱子；缺任何一箱就留到下一班。
    /// </summary>
    private async Task<List<MaterialLot>> ShipReadyDeliveriesAsync(ProductionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = await db.Deliveries.Include(d => d.Lines).Where(d => d.ShippedAt == null).ToListAsync(cancellationToken);
        var ready = new List<MaterialLot>();
        foreach (var delivery in pending)
        {
            var boxIds = delivery.Lines.Select(l => l.BoxLotId).ToList();
            var boxes = await db.Lots.Where(l => boxIds.Contains(l.Id)).ToListAsync(cancellationToken);
            if (boxes.Count == boxIds.Count && boxes.All(b => b.Status == LotStatus.Staged && b.StationCode == settings.BufferStationCode))
            {
                delivery.MarkShipped(now);
                ready.AddRange(boxes);
                logger.LogInformation("Delivery {Delivery} shipped: {Boxes} box(es).", delivery.Number, boxes.Count);
            }
        }

        return ready;
    }

    /// <summary>出貨單的箱子都已經在出貨站、下一班貨車就會載走的箱數。唯讀。</summary>
    private async Task<int> CountReadyDeliveryBoxesAsync(ProductionSettings settings, CancellationToken cancellationToken)
    {
        var boxes = await db.Lots.AsNoTracking()
            .Where(l => l.DeliveryId != null && l.Status != LotStatus.Shipped)
            .Select(l => new { DeliveryId = l.DeliveryId!.Value, l.Status, l.StationCode })
            .ToListAsync(cancellationToken);
        return boxes.GroupBy(b => b.DeliveryId)
            .Where(g => g.All(b => b.Status == LotStatus.Staged && b.StationCode == settings.BufferStationCode))
            .Sum(g => g.Count());
    }
}
