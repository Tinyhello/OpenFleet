using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeparateTemperChangeoverAndTestRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BatchSecondStageSeconds",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "SecondStageName",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "SecondStageSeconds",
                table: "BatchRuns");

            migrationBuilder.AddColumn<double>(
                name: "ChangeoverSeconds",
                table: "ProductionSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 600.0);

            migrationBuilder.AddColumn<double>(
                name: "TestEmptyVehicleSpeed",
                table: "ProductionSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 2.0);

            migrationBuilder.AddColumn<double>(
                name: "TestHandoverSpeed",
                table: "ProductionSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 40.0);

            migrationBuilder.AddColumn<int>(
                name: "BatchMaxPiecesRod24",
                table: "ProcessSteps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ChangeoverDueAt",
                table: "Operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ChangeoverStartedAt",
                table: "Operations",
                type: "timestamp with time zone",
                nullable: true);

            // 既有資料庫：淬火與回火分成兩道工序（兩座爐）。原本第 4 道以後的工序往後移一號，空出來的第 4 道（回火）
            // 由啟動時的 DatabaseInitializer 補上。主鍵不能直接加 1（會撞到下一列），所以先移到 100 以上再移回來。
            // 其餘是使用者 2026-10-10 指定的新數值。全新的資料庫這些表是空的，不受影響。
            migrationBuilder.Sql("""
                UPDATE "ProcessSteps" SET "Sequence" = "Sequence" + 101 WHERE "Sequence" >= 4;
                UPDATE "ProcessSteps" SET "Sequence" = "Sequence" - 100 WHERE "Sequence" >= 100;
                UPDATE "Lots" SET "CurrentStepSequence" = "CurrentStepSequence" + 1 WHERE "CurrentStepSequence" >= 4;
                UPDATE "Operations" SET "StepSequence" = "StepSequence" + 1 WHERE "StepSequence" >= 4;
                UPDATE "BatchRuns" SET "StepSequence" = "StepSequence" + 1 WHERE "StepSequence" >= 4;
                UPDATE "MachineDelays" SET "StepSequence" = "StepSequence" + 1 WHERE "StepSequence" >= 4;
                UPDATE "Stations" SET "Name" = '淬火' WHERE "Code" = 'heat';
                UPDATE "ProcessSteps" SET "TestSpeed" = 20 WHERE "StationCode" IN ('rough', 'fine');
                UPDATE "ProcessSteps" SET "TestSpeed" = 250 WHERE "StationCode" = 'heat';
                UPDATE "ProcessSteps" SET "BatchMinPieces" = 1, "BatchMaxPieces" = 40, "BatchMaxPiecesRod24" = 56 WHERE "StationCode" = 'plating';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChangeoverSeconds",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "TestEmptyVehicleSpeed",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "TestHandoverSpeed",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "BatchMaxPiecesRod24",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "ChangeoverDueAt",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "ChangeoverStartedAt",
                table: "Operations");

            migrationBuilder.AddColumn<double>(
                name: "BatchSecondStageSeconds",
                table: "ProcessSteps",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "SecondStageName",
                table: "ProcessSteps",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SecondStageSeconds",
                table: "BatchRuns",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
