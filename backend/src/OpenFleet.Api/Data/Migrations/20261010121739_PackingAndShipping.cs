using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PackingAndShipping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "TestTruckSpeed",
                table: "ProductionSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 60.0);

            migrationBuilder.AddColumn<double>(
                name: "TruckIntervalSeconds",
                table: "ProductionSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 3600.0);

            // 既有資料庫：流程多了包裝這一道，包好的出貨箱送到出貨站。包裝工序與貨車班次由啟動時的 DatabaseInitializer 補上。
            migrationBuilder.Sql("""
                UPDATE "ProductionSettings" SET "BufferStationCode" = 'shipping' WHERE "BufferStationCode" = 'packing';
                """);

            migrationBuilder.AddColumn<bool>(
                name: "IsPacking",
                table: "ProcessSteps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsBox",
                table: "Lots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Shipments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LotId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LotNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    ShippedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Shipments_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TruckSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    RemainingSeconds = table.Column<double>(type: "double precision", nullable: false),
                    AccruedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastArrivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TruckSchedules", x => x.Id);
                    table.CheckConstraint("CK_TruckSchedules_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_LotId",
                table: "Shipments",
                column: "LotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Shipments");

            migrationBuilder.DropTable(
                name: "TruckSchedules");

            migrationBuilder.DropColumn(
                name: "TestTruckSpeed",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "TruckIntervalSeconds",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "IsPacking",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "IsBox",
                table: "Lots");
        }
    }
}
