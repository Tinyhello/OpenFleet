﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MachinePerStation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 逐件工序一台機台一站（P7 第 1b 步）：站代碼改成「工序代碼-編號」，機台與刀具表跟著重建。
            // 站與機台的代碼一改，在製品、搬運、加工與缺車紀錄都接不上，所以和示範重設一樣清掉生產資料。
            // 倉庫的站（原料倉、成品倉、出貨、刀具庫）保留；庫存餘額與示範的初始庫存、站點、機台與路網在啟動時由示範資料補回。
            migrationBuilder.Sql("""
                TRUNCATE "Lots", "TransportTasks", "OutboxMessages", "InboxMessages", "ScrapRecords", "ProductionOrders",
                         "SalesOrderLines", "SalesOrders", "Deliveries", "Shipments", "InspectionResults", "InventoryMovements",
                         "BatchRuns", "ToolChanges", "MachineDelays", "VehicleWaits", "StockReservations", "StockMovements",
                         "StockBalances", "PurchaseOrders" CASCADE;

                DELETE FROM "MachineTools";
                DELETE FROM "Machines";
                DELETE FROM "Stations" WHERE "Code" NOT IN ('raw-store', 'finished-store', 'shipping', 'tool-store');

                UPDATE "TruckSchedules" SET "RemainingSeconds" = (SELECT "TruckIntervalSeconds" FROM "ProductionSettings"),
                    "AccruedAt" = NULL, "LastArrivedAt" = NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 資料不會還原：這個遷移只清掉生產資料，並把站與機台換成新的代碼。要回到舊結構，請重建資料庫。
        }
    }
}
