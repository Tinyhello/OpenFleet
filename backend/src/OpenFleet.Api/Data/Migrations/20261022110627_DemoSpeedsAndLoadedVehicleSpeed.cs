using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DemoSpeedsAndLoadedVehicleSpeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "TestLoadedVehicleSpeed",
                table: "ProductionSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 5.0);

            // 使用者 2026-10-22：測試模式下一個流程約 2～3 分鐘看完。舊資料庫一次換成新的示範倍率：
            // 各站、貨車與 AGV 交接 3000 倍，空車與載貨 5 倍。之後面板改的值不會再被蓋掉。
            migrationBuilder.Sql("""UPDATE "ProductionSettings" SET "TestEmptyVehicleSpeed" = 5, "TestTruckSpeed" = 3000, "TestHandoverSpeed" = 3000""");
            migrationBuilder.Sql("""UPDATE "ProcessSteps" SET "TestSpeed" = 3000""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TestLoadedVehicleSpeed",
                table: "ProductionSettings");
        }
    }
}
