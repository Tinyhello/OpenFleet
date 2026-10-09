using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PieceMachiningAndTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessSteps_Duration",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "Operations");

            migrationBuilder.RenameColumn(
                name: "DurationSeconds",
                table: "ProcessSteps",
                newName: "ToolLifePieces");

            migrationBuilder.RenameColumn(
                name: "DueAt",
                table: "Operations",
                newName: "PieceStartedAt");

            migrationBuilder.AddColumn<int>(
                name: "OutboundCapacity",
                table: "Stations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HandoverSecondsPerPiece",
                table: "ProductionSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "ToolChangeSeconds",
                table: "ProductionSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "SecondsPerPiece",
                table: "ProcessSteps",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "PiecesCompleted",
                table: "Operations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "Operations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "SecondsPerPiece",
                table: "Operations",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StationTools",
                columns: table => new
                {
                    StationCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PiecesSinceChange = table.Column<int>(type: "integer", nullable: false),
                    ChangeStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ChangeDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationTools", x => x.StationCode);
                    table.ForeignKey(
                        name: "FK_StationTools_Stations_StationCode",
                        column: x => x.StationCode,
                        principalTable: "Stations",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ToolChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StationCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PiecesUsed = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToolChanges_Stations_StationCode",
                        column: x => x.StationCode,
                        principalTable: "Stations",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                });

            // 以下為手動補上的資料修正。產生器把 DurationSeconds→ToolLifePieces、DueAt→PieceStartedAt 當成「改名」，
            // 舊值的意義其實不同，所以在加上 check constraint 之前把既有資料改成新模型的值。
            migrationBuilder.Sql("""
                UPDATE "ProcessSteps"
                SET "SecondsPerPiece" = 2,
                    "ToolLifePieces" = CASE "StationCode" WHEN 'fine' THEN 250 ELSE 500 END;

                UPDATE "ProductionSettings" SET "HandoverSecondsPerPiece" = 5, "ToolChangeSeconds" = 20;

                UPDATE "Stations" SET "OutboundCapacity" = 30 WHERE "InboundCapacity" IS NOT NULL;

                UPDATE "Operations" o
                SET "Quantity" = l."Quantity",
                    "PiecesCompleted" = CASE WHEN o."Status" = 'Completed' THEN l."Quantity" ELSE 0 END,
                    "SecondsPerPiece" = CASE WHEN o."Status" = 'Queued' THEN NULL ELSE 2 END,
                    "PieceStartedAt" = CASE o."Status" WHEN 'Completed' THEN o."CompletedAt" WHEN 'Running' THEN o."StartedAt" ELSE NULL END
                FROM "Lots" l
                WHERE l."Id" = o."LotId";

                INSERT INTO "StationTools" ("StationCode", "PiecesSinceChange")
                SELECT "StationCode", 0 FROM "ProcessSteps";
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessSteps_SecondsPerPiece",
                table: "ProcessSteps",
                sql: "\"SecondsPerPiece\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessSteps_ToolLife",
                table: "ProcessSteps",
                sql: "\"ToolLifePieces\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_ToolChanges_StationCode_Id",
                table: "ToolChanges",
                columns: new[] { "StationCode", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StationTools");

            migrationBuilder.DropTable(
                name: "ToolChanges");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessSteps_SecondsPerPiece",
                table: "ProcessSteps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessSteps_ToolLife",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "OutboundCapacity",
                table: "Stations");

            migrationBuilder.DropColumn(
                name: "HandoverSecondsPerPiece",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "ToolChangeSeconds",
                table: "ProductionSettings");

            migrationBuilder.DropColumn(
                name: "SecondsPerPiece",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "PiecesCompleted",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "SecondsPerPiece",
                table: "Operations");

            migrationBuilder.RenameColumn(
                name: "ToolLifePieces",
                table: "ProcessSteps",
                newName: "DurationSeconds");

            migrationBuilder.RenameColumn(
                name: "PieceStartedAt",
                table: "Operations",
                newName: "DueAt");

            migrationBuilder.AddColumn<int>(
                name: "DurationSeconds",
                table: "Operations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessSteps_Duration",
                table: "ProcessSteps",
                sql: "\"DurationSeconds\" > 0");
        }
    }
}
