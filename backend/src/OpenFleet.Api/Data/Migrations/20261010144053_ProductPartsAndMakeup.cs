using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductPartsAndMakeup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PartCode",
                table: "ProductionSettings");

            migrationBuilder.AlterColumn<string>(
                name: "LotNumber",
                table: "Shipments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AddColumn<string>(
                name: "PartCode",
                table: "Shipments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // 既有的出貨紀錄都是上沖（當時只有這一種零件）。
                defaultValue: "upper-punch");

            migrationBuilder.AddColumn<int>(
                name: "ReworkBatchMinPieces",
                table: "ProductionSettings",
                type: "integer",
                nullable: false,
                // 使用者 2026-10-10：鍍層不良的反工品最少 30 支才重新電鍍。
                defaultValue: 30);

            // 使用者 2026-10-10：粗加工與精加工的測試倍率改成 80。
            migrationBuilder.Sql("""UPDATE "ProcessSteps" SET "TestSpeed" = 80 WHERE "StationCode" IN ('rough', 'fine');""");

            migrationBuilder.AlterColumn<string>(
                name: "LotNumber",
                table: "Lots",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AddColumn<bool>(
                name: "IsMakeup",
                table: "Lots",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PartCode",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "ReworkBatchMinPieces",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "IsMakeup",
                table: "Lots");

            migrationBuilder.AlterColumn<string>(
                name: "LotNumber",
                table: "Shipments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<string>(
                name: "PartCode",
                table: "ProductionSettings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "upper-punch");

            migrationBuilder.AlterColumn<string>(
                name: "LotNumber",
                table: "Lots",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);
        }
    }
}
