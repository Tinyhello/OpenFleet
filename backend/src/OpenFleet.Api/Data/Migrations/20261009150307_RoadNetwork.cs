using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RoadNetwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RouteNodes",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    X = table.Column<double>(type: "double precision", nullable: false),
                    Z = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteNodes", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "RouteEdges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FromCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ToCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteEdges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RouteEdges_RouteNodes_FromCode",
                        column: x => x.FromCode,
                        principalTable: "RouteNodes",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RouteEdges_RouteNodes_ToCode",
                        column: x => x.ToCode,
                        principalTable: "RouteNodes",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RouteEdges_FromCode_ToCode",
                table: "RouteEdges",
                columns: new[] { "FromCode", "ToCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RouteEdges_ToCode",
                table: "RouteEdges",
                column: "ToCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RouteEdges");

            migrationBuilder.DropTable(
                name: "RouteNodes");
        }
    }
}
