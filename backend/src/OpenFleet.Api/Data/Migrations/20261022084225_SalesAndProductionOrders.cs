using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SalesAndProductionOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProductionOrderId",
                table: "Lots",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalesOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Customer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RequestKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderLines", x => x.Id);
                    table.CheckConstraint("CK_SalesOrderLines_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_Items_ItemCode",
                        column: x => x.ItemCode,
                        principalTable: "Items",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductionOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SalesOrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    PartCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    BomVersion = table.Column<int>(type: "integer", nullable: false),
                    Spec_SpecCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Spec_SpecName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Spec_RodDiameterMm = table.Column<int>(type: "integer", nullable: false),
                    Spec_PiecesPerBin = table.Column<int>(type: "integer", nullable: false),
                    Spec_DieOuterDiameterMm = table.Column<int>(type: "integer", nullable: false),
                    Spec_UpperBlankLengthMm = table.Column<int>(type: "integer", nullable: false),
                    Spec_DieBlankLengthMm = table.Column<int>(type: "integer", nullable: false),
                    Spec_LowerBlankLengthMm = table.Column<int>(type: "integer", nullable: false),
                    Spec_RoughSecondsPerPiece = table.Column<double>(type: "double precision", nullable: true),
                    Spec_FinishSecondsPerPiece = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionOrders", x => x.Id);
                    table.CheckConstraint("CK_ProductionOrders_Parent", "(\"Kind\" = 'Main') = (\"ParentId\" IS NULL)");
                    table.CheckConstraint("CK_ProductionOrders_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_ProductionOrders_Items_ItemCode",
                        column: x => x.ItemCode,
                        principalTable: "Items",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_ProductionOrders_ParentId",
                        column: x => x.ParentId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_SalesOrderLines_SalesOrderLineId",
                        column: x => x.SalesOrderLineId,
                        principalTable: "SalesOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScrapRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MainOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    MakeupOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InspectionRound = table.Column<int>(type: "integer", nullable: false),
                    PartCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapRecords", x => x.Id);
                    table.CheckConstraint("CK_ScrapRecords_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_ScrapRecords_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScrapRecords_ProductionOrders_MainOrderId",
                        column: x => x.MainOrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScrapRecords_ProductionOrders_MakeupOrderId",
                        column: x => x.MakeupOrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScrapRecords_ProductionOrders_SourceOrderId",
                        column: x => x.SourceOrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lots_ProductionOrderId",
                table: "Lots",
                column: "ProductionOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_ItemCode",
                table: "ProductionOrders",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_Number",
                table: "ProductionOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_ParentId",
                table: "ProductionOrders",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_SalesOrderLineId",
                table: "ProductionOrders",
                column: "SalesOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_ItemCode",
                table: "SalesOrderLines",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_SalesOrderId_LineNumber",
                table: "SalesOrderLines",
                columns: new[] { "SalesOrderId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_Number",
                table: "SalesOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_RequestKey",
                table: "SalesOrders",
                column: "RequestKey",
                unique: true,
                filter: "\"RequestKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapRecords_LotId_InspectionRound",
                table: "ScrapRecords",
                columns: new[] { "LotId", "InspectionRound" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrapRecords_MainOrderId",
                table: "ScrapRecords",
                column: "MainOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapRecords_MakeupOrderId",
                table: "ScrapRecords",
                column: "MakeupOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapRecords_SourceOrderId",
                table: "ScrapRecords",
                column: "SourceOrderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Lots_ProductionOrders_ProductionOrderId",
                table: "Lots",
                column: "ProductionOrderId",
                principalTable: "ProductionOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Lots_ProductionOrders_ProductionOrderId",
                table: "Lots");

            migrationBuilder.DropTable(
                name: "ScrapRecords");

            migrationBuilder.DropTable(
                name: "ProductionOrders");

            migrationBuilder.DropTable(
                name: "SalesOrderLines");

            migrationBuilder.DropTable(
                name: "SalesOrders");

            migrationBuilder.DropIndex(
                name: "IX_Lots_ProductionOrderId",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "ProductionOrderId",
                table: "Lots");
        }
    }
}
