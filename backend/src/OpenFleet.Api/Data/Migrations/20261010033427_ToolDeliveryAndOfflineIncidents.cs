using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ToolDeliveryAndOfflineIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "HandoverSecondsPerPiece",
                table: "ProductionSettings",
                newName: "PieceHandlingSeconds");

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "VehicleWaits",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // 區分原因之前的紀錄一律當成缺車。
                defaultValue: "VehiclesBusy");

            migrationBuilder.AlterColumn<Guid>(
                name: "LotId",
                table: "TransportTasks",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SpareDeliveredAt",
                table: "StationTools",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SpareRequestedAt",
                table: "StationTools",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WaitingSince",
                table: "StationTools",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AgvHandoverSeconds",
                table: "ProductionSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "ToolDeliveryEnabled",
                table: "ProductionSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ToolRequestThresholdPieces",
                table: "ProductionSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ToolStoreStationCode",
                table: "ProductionSettings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "HandlingSeconds",
                table: "Operations",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "MachineDelays",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "VehiclesBusy");

            // 既有資料庫的設定列：新欄位補上示範值（使用者 2026-10-10：AGV 交接一框 30 秒、換刀 90 秒）。
            // 換刀時間只更新還停在舊暫定值 20 秒的；新資料庫由 DatabaseInitializer 直接寫入。
            migrationBuilder.Sql("""
                UPDATE "ProductionSettings"
                SET "AgvHandoverSeconds" = 30, "ToolRequestThresholdPieces" = 50, "ToolDeliveryEnabled" = TRUE, "ToolStoreStationCode" = 'tool-store';
                UPDATE "ProductionSettings" SET "ToolChangeSeconds" = 90 WHERE "ToolChangeSeconds" = 20;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Reason",
                table: "VehicleWaits");

            migrationBuilder.DropColumn(
                name: "SpareDeliveredAt",
                table: "StationTools");

            migrationBuilder.DropColumn(
                name: "SpareRequestedAt",
                table: "StationTools");

            migrationBuilder.DropColumn(
                name: "WaitingSince",
                table: "StationTools");

            migrationBuilder.DropColumn(
                name: "AgvHandoverSeconds",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "ToolDeliveryEnabled",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "ToolRequestThresholdPieces",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "ToolStoreStationCode",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "HandlingSeconds",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "MachineDelays");

            migrationBuilder.RenameColumn(
                name: "PieceHandlingSeconds",
                table: "ProductionSettings",
                newName: "HandoverSecondsPerPiece");

            migrationBuilder.AlterColumn<Guid>(
                name: "LotId",
                table: "TransportTasks",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
