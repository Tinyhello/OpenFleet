using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackEndProcessesAndBins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessSteps_SecondsPerPiece",
                table: "ProcessSteps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessSteps_ToolLife",
                table: "ProcessSteps");

            migrationBuilder.AddColumn<int>(
                name: "BatchMaxPieces",
                table: "ProcessSteps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BatchMinPieces",
                table: "ProcessSteps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "BatchSecondStageSeconds",
                table: "ProcessSteps",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "BatchSeconds",
                table: "ProcessSteps",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "ProcessSteps",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Piece");

            migrationBuilder.AddColumn<string>(
                name: "SecondStageName",
                table: "ProcessSteps",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StageName",
                table: "ProcessSteps",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsesTool",
                table: "ProcessSteps",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchRunId",
                table: "Operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BatchNumber",
                table: "Lots",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "BinCount",
                table: "Lots",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BinIndex",
                table: "Lots",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "RodDiameterMm",
                table: "Lots",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateTable(
                name: "BatchRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StationCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StepSequence = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    TotalSeconds = table.Column<double>(type: "double precision", nullable: false),
                    SecondStageSeconds = table.Column<double>(type: "double precision", nullable: false),
                    RemainingSeconds = table.Column<double>(type: "double precision", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AccruedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatchRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BatchRuns_Stations_StationCode",
                        column: x => x.StationCode,
                        principalTable: "Stations",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            // 既有資料：原本的兩道工序都是有刀具的逐件工序（上面的欄位預設值）；既有的每一筆物料各自算一個批次、一框。
            // 流程終點從淬火站入口改到品管站入口（淬火之後的工序由 DatabaseInitializer 補上）。
            migrationBuilder.Sql("""
                UPDATE "Lots" SET "BatchNumber" = "LotNumber" WHERE "BatchNumber" = '';
                UPDATE "ProductionSettings" SET "BufferStationCode" = 'qc' WHERE "BufferStationCode" = 'heat';
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessSteps_Batch",
                table: "ProcessSteps",
                sql: "\"Kind\" <> 'Batch' OR (\"BatchMinPieces\" > 0 AND \"BatchMaxPieces\" >= \"BatchMinPieces\" AND \"BatchSeconds\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessSteps_SecondsPerPiece",
                table: "ProcessSteps",
                sql: "\"Kind\" <> 'Piece' OR \"SecondsPerPiece\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessSteps_ToolLife",
                table: "ProcessSteps",
                sql: "NOT \"UsesTool\" OR \"ToolLifePieces\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_BatchNumber",
                table: "Lots",
                column: "BatchNumber");

            migrationBuilder.CreateIndex(
                name: "UX_BatchRuns_RunningStation",
                table: "BatchRuns",
                column: "StationCode",
                unique: true,
                filter: "\"CompletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BatchRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessSteps_Batch",
                table: "ProcessSteps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessSteps_SecondsPerPiece",
                table: "ProcessSteps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessSteps_ToolLife",
                table: "ProcessSteps");

            migrationBuilder.DropIndex(
                name: "IX_Lots_BatchNumber",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "BatchMaxPieces",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "BatchMinPieces",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "BatchSecondStageSeconds",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "BatchSeconds",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "SecondStageName",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "StageName",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "UsesTool",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "BatchRunId",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "BatchNumber",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "BinCount",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "BinIndex",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "RodDiameterMm",
                table: "Lots");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessSteps_SecondsPerPiece",
                table: "ProcessSteps",
                sql: "\"SecondsPerPiece\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessSteps_ToolLife",
                table: "ProcessSteps",
                sql: "\"ToolLifePieces\" > 0");
        }
    }
}
