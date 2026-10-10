using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InspectionAndRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DefectRate",
                table: "ProductionSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.1);

            migrationBuilder.AddColumn<int>(
                name: "MaxReworkCount",
                table: "ProductionSettings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "IsInspection",
                table: "ProcessSteps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ReworkCount",
                table: "Lots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 既有資料庫：流程多了品管這一道，合格的料改送到包裝站入口暫存。品管工序由啟動時的 DatabaseInitializer 補上。
            migrationBuilder.Sql("""
                UPDATE "ProductionSettings" SET "BufferStationCode" = 'packing' WHERE "BufferStationCode" = 'qc';
                """);

            migrationBuilder.CreateTable(
                name: "InspectionResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LotId = table.Column<Guid>(type: "uuid", nullable: false),
                    StationCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReworkRound = table.Column<int>(type: "integer", nullable: false),
                    Inspected = table.Column<int>(type: "integer", nullable: false),
                    Passed = table.Column<int>(type: "integer", nullable: false),
                    Reworked = table.Column<int>(type: "integer", nullable: false),
                    Scrapped = table.Column<int>(type: "integer", nullable: false),
                    SurfaceDefects = table.Column<int>(type: "integer", nullable: false),
                    PlatingDefects = table.Column<int>(type: "integer", nullable: false),
                    DimensionDefects = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InspectionResults_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InspectionResults_LotId",
                table: "InspectionResults",
                column: "LotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InspectionResults");

            migrationBuilder.DropColumn(
                name: "DefectRate",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "MaxReworkCount",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "IsInspection",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "ReworkCount",
                table: "Lots");
        }
    }
}
