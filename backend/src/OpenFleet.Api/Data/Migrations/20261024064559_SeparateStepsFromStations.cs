using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeparateStepsFromStations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProcessSteps_Stations_StationCode",
                table: "ProcessSteps");

            migrationBuilder.DropIndex(
                name: "IX_ProcessSteps_StationCode",
                table: "ProcessSteps");

            migrationBuilder.RenameColumn(
                name: "StationCode",
                table: "ProcessSteps",
                newName: "Code");

            migrationBuilder.AlterColumn<string>(
                name: "ToStationCode",
                table: "TransportTasks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AddColumn<int>(
                name: "ToStepSequence",
                table: "TransportTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StepSequence",
                table: "Stations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "ProcessSteps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // 舊資料庫：工序的代碼就是原本的站點代碼（cutting、rough…），名稱與工站的站名相同；
            // 每道工序的站就是同代碼的站（一道工序一站），把工站對應到工序的序號。
            migrationBuilder.Sql("""
                UPDATE "ProcessSteps" s SET "Name" = st."Name"
                FROM "Stations" st WHERE st."Code" = s."Code";

                UPDATE "Stations" st SET "StepSequence" = s."Sequence"
                FROM "ProcessSteps" s WHERE s."Code" = st."Code";
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_TransportTasks_Destination",
                table: "TransportTasks",
                sql: "\"ToStationCode\" IS NOT NULL OR \"ToStepSequence\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessSteps_Code",
                table: "ProcessSteps",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 待派工的送到工序搬運：退回舊的寫法，目的站就是那道工序的站。
            migrationBuilder.Sql("""
                UPDATE "TransportTasks" t SET "ToStationCode" = s."Code"
                FROM "ProcessSteps" s WHERE t."ToStationCode" IS NULL AND t."ToStepSequence" = s."Sequence";
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_TransportTasks_Destination",
                table: "TransportTasks");

            migrationBuilder.DropIndex(
                name: "IX_ProcessSteps_Code",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "ToStepSequence",
                table: "TransportTasks");

            migrationBuilder.DropColumn(
                name: "StepSequence",
                table: "Stations");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "ProcessSteps");

            migrationBuilder.RenameColumn(
                name: "Code",
                table: "ProcessSteps",
                newName: "StationCode");

            migrationBuilder.AlterColumn<string>(
                name: "ToStationCode",
                table: "TransportTasks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessSteps_StationCode",
                table: "ProcessSteps",
                column: "StationCode");

            migrationBuilder.AddForeignKey(
                name: "FK_ProcessSteps_Stations_StationCode",
                table: "ProcessSteps",
                column: "StationCode",
                principalTable: "Stations",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
