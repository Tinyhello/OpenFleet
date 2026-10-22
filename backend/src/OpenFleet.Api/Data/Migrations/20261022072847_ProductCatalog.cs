using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RodSizes",
                columns: table => new
                {
                    DiameterMm = table.Column<int>(type: "integer", nullable: false),
                    PiecesPerBin = table.Column<int>(type: "integer", nullable: false),
                    UpperBlankLengthMm = table.Column<int>(type: "integer", nullable: false),
                    DieBlankLengthMm = table.Column<int>(type: "integer", nullable: false),
                    LowerBlankLengthMm = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RodSizes", x => x.DiameterMm);
                    table.CheckConstraint("CK_RodSizes_BlankLength", "\"UpperBlankLengthMm\" > 0 AND \"DieBlankLengthMm\" > 0 AND \"LowerBlankLengthMm\" > 0");
                    table.CheckConstraint("CK_RodSizes_PiecesPerBin", "\"PiecesPerBin\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "PunchSpecs",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Shape = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RodDiameterMm = table.Column<int>(type: "integer", nullable: false),
                    UpperBlankLengthMm = table.Column<int>(type: "integer", nullable: true),
                    DieBlankLengthMm = table.Column<int>(type: "integer", nullable: true),
                    LowerBlankLengthMm = table.Column<int>(type: "integer", nullable: true),
                    RoughSecondsPerPiece = table.Column<double>(type: "double precision", nullable: true),
                    FinishSecondsPerPiece = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PunchSpecs", x => x.Code);
                    table.CheckConstraint("CK_PunchSpecs_Overrides", "(\"UpperBlankLengthMm\" IS NULL OR \"UpperBlankLengthMm\" > 0) AND (\"DieBlankLengthMm\" IS NULL OR \"DieBlankLengthMm\" > 0) AND (\"LowerBlankLengthMm\" IS NULL OR \"LowerBlankLengthMm\" > 0) AND (\"RoughSecondsPerPiece\" IS NULL OR \"RoughSecondsPerPiece\" > 0) AND (\"FinishSecondsPerPiece\" IS NULL OR \"FinishSecondsPerPiece\" > 0)");
                    table.ForeignKey(
                        name: "FK_PunchSpecs_RodSizes_RodDiameterMm",
                        column: x => x.RodDiameterMm,
                        principalTable: "RodSizes",
                        principalColumn: "DiameterMm",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    PunchSpecCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PartCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DiameterMm = table.Column<int>(type: "integer", nullable: true),
                    LengthMm = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Code);
                    table.ForeignKey(
                        name: "FK_Items_PunchSpecs_PunchSpecCode",
                        column: x => x.PunchSpecCode,
                        principalTable: "PunchSpecs",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BomVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BomVersions", x => x.Id);
                    table.CheckConstraint("CK_BomVersions_Version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_BomVersions_Items_ItemCode",
                        column: x => x.ItemCode,
                        principalTable: "Items",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BomLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BomVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    ChildItemCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BomLine", x => x.Id);
                    table.CheckConstraint("CK_BomLines_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_BomLine_BomVersions_BomVersionId",
                        column: x => x.BomVersionId,
                        principalTable: "BomVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BomLine_Items_ChildItemCode",
                        column: x => x.ChildItemCode,
                        principalTable: "Items",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BomLine_BomVersionId_ChildItemCode",
                table: "BomLine",
                columns: new[] { "BomVersionId", "ChildItemCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BomLine_ChildItemCode",
                table: "BomLine",
                column: "ChildItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_BomVersions_ItemCode_Version",
                table: "BomVersions",
                columns: new[] { "ItemCode", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_Kind_PunchSpecCode",
                table: "Items",
                columns: new[] { "Kind", "PunchSpecCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_PunchSpecCode",
                table: "Items",
                column: "PunchSpecCode");

            migrationBuilder.CreateIndex(
                name: "IX_PunchSpecs_RodDiameterMm",
                table: "PunchSpecs",
                column: "RodDiameterMm");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BomLine");

            migrationBuilder.DropTable(
                name: "BomVersions");

            migrationBuilder.DropTable(
                name: "Items");

            migrationBuilder.DropTable(
                name: "PunchSpecs");

            migrationBuilder.DropTable(
                name: "RodSizes");
        }
    }
}
