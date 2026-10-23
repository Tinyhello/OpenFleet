using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TestFurnaceWait : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "TestFurnaceWaitSeconds",
                table: "ProductionSettings",
                type: "double precision",
                nullable: false,
                // 使用者 2026-10-23：測試模式下爐前有料、等超過 1 分鐘就開爐。舊資料庫也用 60 秒。
                defaultValue: 60.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TestFurnaceWaitSeconds",
                table: "ProductionSettings");
        }
    }
}
