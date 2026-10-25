using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LayoutAppliedSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppliedDocument",
                table: "FactoryLayouts",
                type: "jsonb",
                nullable: true);

            // 回填：目前使用中的佈局，工廠正在跑的就是它當時存的文件（升級前存檔即套用的語意）。
            // 預設佈局的文件若被程式改過，啟動時 Reconcile 比對雜湊會重新套用並更新快照。
            migrationBuilder.Sql("""UPDATE "FactoryLayouts" SET "AppliedDocument" = "Document" WHERE "IsActive";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppliedDocument",
                table: "FactoryLayouts");
        }
    }
}
