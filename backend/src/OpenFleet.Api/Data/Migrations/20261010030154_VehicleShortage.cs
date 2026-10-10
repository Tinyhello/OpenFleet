using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class VehicleShortage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MachineDelays",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StationCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StepSequence = table.Column<int>(type: "integer", nullable: false),
                    Cause = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SimulatedSeconds = table.Column<double>(type: "double precision", nullable: false),
                    AccruedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineDelays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MachineDelays_Stations_StationCode",
                        column: x => x.StationCode,
                        principalTable: "Stations",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MachineDelays_TransportTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "TransportTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleWaits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SimulatedSeconds = table.Column<double>(type: "double precision", nullable: false),
                    AccruedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleWaits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleWaits_TransportTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "TransportTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MachineDelays_StepSequence",
                table: "MachineDelays",
                column: "StepSequence");

            migrationBuilder.CreateIndex(
                name: "IX_MachineDelays_TaskId",
                table: "MachineDelays",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "UX_MachineDelays_OpenStation",
                table: "MachineDelays",
                column: "StationCode",
                unique: true,
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_VehicleWaits_OpenTask",
                table: "VehicleWaits",
                column: "TaskId",
                unique: true,
                filter: "\"EndedAt\" IS NULL");

            // 示範加工時間改為粗加工 30 秒、精加工 60 秒（使用者 2026-10-10）。
            // 只更新還停在舊暫定值 2 秒的資料庫；面板改過的值不動。新資料庫由 DatabaseInitializer 直接寫入新值。
            migrationBuilder.Sql("""
                UPDATE "ProcessSteps" SET "SecondsPerPiece" = 30 WHERE "StationCode" = 'rough' AND "SecondsPerPiece" = 2;
                UPDATE "ProcessSteps" SET "SecondsPerPiece" = 60 WHERE "StationCode" = 'fine' AND "SecondsPerPiece" = 2;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MachineDelays");

            migrationBuilder.DropTable(
                name: "VehicleWaits");
        }
    }
}
