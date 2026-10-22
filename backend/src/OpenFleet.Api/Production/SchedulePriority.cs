using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;

namespace OpenFleet.Api.Production;

/// <summary>
/// 一框的排程優先順序（使用者 2026-10-22：排隊的框交期早的先做；負責人可以把訂單設成急單，排在所有訂單前面）。
/// 急單在前；同為急單或同為一般訂單時，交期早的在前，沒有交期的排在有交期的後面。
/// 一樣的優先順序之間照各自原本的規則：機台看換線與同批次、爐與圓棒看到站或建立的順序、派車看搬運的種類與建立時間。
/// 沒有訂單的框（測試用的批次、送成品倉的箱子）和沒有交期的一般訂單一樣。
/// </summary>
public readonly record struct SchedulePriority(bool Rush, DateOnly? DueDate) : IComparable<SchedulePriority>
{
    public static readonly SchedulePriority None = new(false, null);

    /// <summary>越前面越小：急單、交期早、沒有交期。</summary>
    public int CompareTo(SchedulePriority other)
    {
        if (Rush != other.Rush)
        {
            return Rush ? -1 : 1;
        }

        if (DueDate == other.DueDate)
        {
            return 0;
        }

        if (DueDate is null)
        {
            return 1;
        }

        return other.DueDate is null ? -1 : DueDate.Value.CompareTo(other.DueDate.Value);
    }
}

/// <summary>查框的優先順序（唯讀）：工單的框看工單所屬的客戶訂單（補做工單也是），訂單的出貨箱看出貨單所屬的訂單。</summary>
internal static class SchedulePriorities
{
    public static async Task<Dictionary<Guid, SchedulePriority>> ForLotsAsync(
        OpenFleetDbContext db, IEnumerable<MaterialLot> lots, CancellationToken cancellationToken)
    {
        var list = lots.DistinctBy(l => l.Id).ToList();
        var workOrderIds = list.Select(l => l.ProductionOrderId).OfType<Guid>().Distinct().ToList();
        var deliveryIds = list.Select(l => l.DeliveryId).OfType<Guid>().Distinct().ToList();

        var byWorkOrder = workOrderIds.Count == 0
            ? new Dictionary<Guid, SchedulePriority>()
            : await (from workOrder in db.ProductionOrders.AsNoTracking()
                     join line in db.SalesOrderLines.AsNoTracking() on workOrder.SalesOrderLineId equals line.Id
                     join order in db.SalesOrders.AsNoTracking() on line.SalesOrderId equals order.Id
                     where workOrderIds.Contains(workOrder.Id)
                     select new { workOrder.Id, Rush = order.RushRequestedAt != null, order.DueDate })
                .ToDictionaryAsync(x => x.Id, x => new SchedulePriority(x.Rush, x.DueDate), cancellationToken);
        var byDelivery = deliveryIds.Count == 0
            ? new Dictionary<Guid, SchedulePriority>()
            : await (from delivery in db.Deliveries.AsNoTracking()
                     join order in db.SalesOrders.AsNoTracking() on delivery.SalesOrderId equals order.Id
                     where deliveryIds.Contains(delivery.Id)
                     select new { delivery.Id, Rush = order.RushRequestedAt != null, order.DueDate })
                .ToDictionaryAsync(x => x.Id, x => new SchedulePriority(x.Rush, x.DueDate), cancellationToken);

        return list.ToDictionary(lot => lot.Id, lot =>
            lot.ProductionOrderId is { } workOrderId && byWorkOrder.TryGetValue(workOrderId, out var fromWorkOrder) ? fromWorkOrder
            : lot.DeliveryId is { } deliveryId && byDelivery.TryGetValue(deliveryId, out var fromDelivery) ? fromDelivery
            : SchedulePriority.None);
    }
}
