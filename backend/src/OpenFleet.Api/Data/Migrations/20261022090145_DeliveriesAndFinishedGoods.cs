using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeliveriesAndFinishedGoods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockBalances_ItemCode_StationCode",
                table: "StockBalances");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ScrapRecords_Quantity",
                table: "ScrapRecords");

            migrationBuilder.AddColumn<string>(
                name: "Owner",
                table: "StockBalances",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "MakeupOrderId",
                table: "ScrapRecords",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "CoveredByExtra",
                table: "ScrapRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PartialApprovedAt",
                table: "SalesOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PartialApprovedBy",
                table: "SalesOrders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "SalesOrders",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<int>(
                name: "ExtraQuantity",
                table: "ProductionOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "DeliveryId",
                table: "Lots",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPartial = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ShippedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deliveries_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SurplusTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoxLotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Customer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    PartCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurplusTransfers", x => x.Id);
                    table.CheckConstraint("CK_SurplusTransfers_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_SurplusTransfers_Items_ItemCode",
                        column: x => x.ItemCode,
                        principalTable: "Items",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SurplusTransfers_Lots_BoxLotId",
                        column: x => x.BoxLotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SurplusTransfers_SalesOrderLines_SalesOrderLineId",
                        column: x => x.SalesOrderLineId,
                        principalTable: "SalesOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SurplusTransfers_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    BoxLotId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryLine", x => x.Id);
                    table.CheckConstraint("CK_DeliveryLines_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_DeliveryLine_Deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "Deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeliveryLine_Lots_BoxLotId",
                        column: x => x.BoxLotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeliveryLine_SalesOrderLines_SalesOrderLineId",
                        column: x => x.SalesOrderLineId,
                        principalTable: "SalesOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockBalances_ItemCode_StationCode_Owner",
                table: "StockBalances",
                columns: new[] { "ItemCode", "StationCode", "Owner" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ScrapRecords_Makeup",
                table: "ScrapRecords",
                sql: "(\"MakeupOrderId\" IS NULL) = (\"CoveredByExtra\" = \"Quantity\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ScrapRecords_Quantity",
                table: "ScrapRecords",
                sql: "\"Quantity\" > 0 AND \"CoveredByExtra\" >= 0 AND \"CoveredByExtra\" <= \"Quantity\"");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_DeliveryId",
                table: "Lots",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_Number",
                table: "Deliveries",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_SalesOrderId",
                table: "Deliveries",
                column: "SalesOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLine_BoxLotId",
                table: "DeliveryLine",
                column: "BoxLotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLine_DeliveryId",
                table: "DeliveryLine",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLine_SalesOrderLineId",
                table: "DeliveryLine",
                column: "SalesOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_SurplusTransfers_BoxLotId",
                table: "SurplusTransfers",
                column: "BoxLotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SurplusTransfers_ItemCode",
                table: "SurplusTransfers",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_SurplusTransfers_SalesOrderId",
                table: "SurplusTransfers",
                column: "SalesOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SurplusTransfers_SalesOrderLineId",
                table: "SurplusTransfers",
                column: "SalesOrderLineId");

            migrationBuilder.AddForeignKey(
                name: "FK_Lots_Deliveries_DeliveryId",
                table: "Lots",
                column: "DeliveryId",
                principalTable: "Deliveries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Lots_Deliveries_DeliveryId",
                table: "Lots");

            migrationBuilder.DropTable(
                name: "DeliveryLine");

            migrationBuilder.DropTable(
                name: "SurplusTransfers");

            migrationBuilder.DropTable(
                name: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_StockBalances_ItemCode_StationCode_Owner",
                table: "StockBalances");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ScrapRecords_Makeup",
                table: "ScrapRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ScrapRecords_Quantity",
                table: "ScrapRecords");

            migrationBuilder.DropIndex(
                name: "IX_Lots_DeliveryId",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "Owner",
                table: "StockBalances");

            migrationBuilder.DropColumn(
                name: "CoveredByExtra",
                table: "ScrapRecords");

            migrationBuilder.DropColumn(
                name: "PartialApprovedAt",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "PartialApprovedBy",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "ExtraQuantity",
                table: "ProductionOrders");

            migrationBuilder.DropColumn(
                name: "DeliveryId",
                table: "Lots");

            migrationBuilder.AlterColumn<Guid>(
                name: "MakeupOrderId",
                table: "ScrapRecords",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockBalances_ItemCode_StationCode",
                table: "StockBalances",
                columns: new[] { "ItemCode", "StationCode" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ScrapRecords_Quantity",
                table: "ScrapRecords",
                sql: "\"Quantity\" > 0");
        }
    }
}
