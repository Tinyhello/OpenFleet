using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CuttingStation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCutting",
                table: "ProcessSteps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SpecCode",
                table: "Lots",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CuttingJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BlankItemCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    BarItemCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    BlankLengthMm = table.Column<int>(type: "integer", nullable: false),
                    PiecesPerBar = table.Column<int>(type: "integer", nullable: false),
                    Bars = table.Column<int>(type: "integer", nullable: false),
                    BarsReserved = table.Column<int>(type: "integer", nullable: false),
                    PiecesToCut = table.Column<int>(type: "integer", nullable: false),
                    ExtraBlanks = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CuttingJobs", x => x.Id);
                    table.CheckConstraint("CK_CuttingJobs_Quantities", "\"Bars\" >= 0 AND \"BarsReserved\" >= 0 AND \"BarsReserved\" <= \"Bars\" AND \"PiecesPerBar\" > 0 AND \"ExtraBlanks\" >= 0");
                    table.ForeignKey(
                        name: "FK_CuttingJobs_Items_BarItemCode",
                        column: x => x.BarItemCode,
                        principalTable: "Items",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CuttingJobs_Items_BlankItemCode",
                        column: x => x.BlankItemCode,
                        principalTable: "Items",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CuttingJobs_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CuttingJobs_BarItemCode",
                table: "CuttingJobs",
                column: "BarItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_CuttingJobs_BlankItemCode",
                table: "CuttingJobs",
                column: "BlankItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_CuttingJobs_LotId",
                table: "CuttingJobs",
                column: "LotId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CuttingJobs");

            migrationBuilder.DropColumn(
                name: "IsCutting",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "SpecCode",
                table: "Lots");
        }
    }
}
