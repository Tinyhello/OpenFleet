using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StationTestSpeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SpeedChangedAt",
                table: "ProductionSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TestMode",
                table: "ProductionSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "TestSpeed",
                table: "ProcessSteps",
                type: "double precision",
                nullable: false,
                defaultValue: 1.0);

            // 已經存在的工序補上示範的專用倍率（和 DatabaseInitializer 的示範值相同）。
            migrationBuilder.Sql("""
                UPDATE "ProcessSteps" SET "TestSpeed" = CASE "StationCode"
                    WHEN 'rough' THEN 10 WHEN 'fine' THEN 20 WHEN 'heat' THEN 200
                    WHEN 'plating' THEN 60 WHEN 'polish' THEN 10 ELSE 1 END;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessSteps_TestSpeed",
                table: "ProcessSteps",
                sql: "\"TestSpeed\" >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessSteps_TestSpeed",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "SpeedChangedAt",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "TestMode",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "TestSpeed",
                table: "ProcessSteps");
        }
    }
}
