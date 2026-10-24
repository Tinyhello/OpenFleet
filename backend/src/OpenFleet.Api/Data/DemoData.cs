using Microsoft.EntityFrameworkCore;

namespace OpenFleet.Api.Data;

/// <summary>
/// 示範資料的重設（P7 第 2 步 5.9）：示範重設（<c>/api/demo/reset</c>）與佈局套用共用同一段清空的 SQL。
/// </summary>
public static class DemoData
{
    /// <summary>清空訂單、批次、搬運、庫存與採購，刀具計數與換刀紀錄歸零，貨車從頭倒數。不寫入新的資料。</summary>
    public static async Task ClearProductionAsync(OpenFleetDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE "Lots", "TransportTasks", "OutboxMessages", "InboxMessages", "ScrapRecords", "ProductionOrders", "SalesOrderLines", "SalesOrders" CASCADE""", cancellationToken);

        // 刀具計數與換刀紀錄也歸零，機台回到剛換好刀的狀態。
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE "ToolChanges", "BatchRuns"; UPDATE "MachineTools" SET "PiecesSinceChange" = 0, "ChangeStartedAt" = NULL, "ChangeDueAt" = NULL, "SpareRequestedAt" = NULL, "SpareDeliveredAt" = NULL, "WaitingSince" = NULL""",
            cancellationToken);

        // 貨車從頭倒數。
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE "TruckSchedules" SET "RemainingSeconds" = (SELECT "TruckIntervalSeconds" FROM "ProductionSettings"), "AccruedAt" = NULL, "LastArrivedAt" = NULL""",
            cancellationToken);

        // 庫存與採購清空；初始庫存由呼叫端在需要時重新放入。
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE "StockReservations", "StockMovements", "StockBalances", "PurchaseOrders" """, cancellationToken);
    }

    /// <summary>示範重設：清空後放回示範的初始庫存。</summary>
    public static async Task ResetAsync(OpenFleetDbContext db, CancellationToken cancellationToken)
    {
        await ClearProductionAsync(db, cancellationToken);
        await DatabaseInitializer.SeedInventoryAsync(db);
        await db.SaveChangesAsync(cancellationToken);
    }
}
