using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <summary>
    /// P6 第 1 步：機台表（粗加工、精加工各兩台車床，其他逐件工序的站一台）。
    /// 舊資料庫的資料搬過來：每一站原本的那一台變成 1 號機（刀具狀態、換刀紀錄、加工紀錄、送刀任務、缺車停機都接到 1 號機），
    /// 粗加工、精加工另外加一台 2 號機（新刀）；這兩站的待加工容量從一台的 30 件變成兩台的 60 件。
    /// 機台的名稱由啟動時的示範資料補上。
    /// </summary>
    public partial class MachinesAndSecondLathes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Machines",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StationCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Machines", x => x.Code);
                    table.CheckConstraint("CK_Machines_Number", "\"Number\" >= 1");
                    table.ForeignKey(
                        name: "FK_Machines_Stations_StationCode",
                        column: x => x.StationCode,
                        principalTable: "Stations",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Machines_StationCode_Number",
                table: "Machines",
                columns: new[] { "StationCode", "Number" },
                unique: true);

            // 舊資料庫：逐件工序的每一站原本那一台是 1 號機；粗加工、精加工再加一台 2 號機。新資料庫這時還沒有工序，什麼都不做。
            migrationBuilder.Sql("""
                INSERT INTO "Machines" ("Code", "StationCode", "Number", "Name")
                SELECT s."StationCode" || '-1', s."StationCode", 1, st."Name"
                FROM "ProcessSteps" s JOIN "Stations" st ON st."Code" = s."StationCode"
                WHERE s."Kind" = 'Piece';

                INSERT INTO "Machines" ("Code", "StationCode", "Number", "Name")
                SELECT s."StationCode" || '-2', s."StationCode", 2, st."Name"
                FROM "ProcessSteps" s JOIN "Stations" st ON st."Code" = s."StationCode"
                WHERE s."Kind" = 'Piece' AND s."StationCode" IN ('rough', 'fine');

                UPDATE "Stations" SET "InboundCapacity" = 60, "OutboundCapacity" = 60
                WHERE "Code" IN ('rough', 'fine') AND "InboundCapacity" = 30;
                """);

            // 刀具：原本一站一把，變成 1 號機的刀；2 號機是一把新刀。
            migrationBuilder.CreateTable(
                name: "MachineTools",
                columns: table => new
                {
                    MachineCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PiecesSinceChange = table.Column<int>(type: "integer", nullable: false),
                    ChangeStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ChangeDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SpareRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SpareDeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WaitingSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineTools", x => x.MachineCode);
                    table.ForeignKey(
                        name: "FK_MachineTools_Machines_MachineCode",
                        column: x => x.MachineCode,
                        principalTable: "Machines",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO "MachineTools" ("MachineCode", "PiecesSinceChange", "ChangeStartedAt", "ChangeDueAt", "SpareRequestedAt", "SpareDeliveredAt", "WaitingSince")
                SELECT m."Code", t."PiecesSinceChange", t."ChangeStartedAt", t."ChangeDueAt", t."SpareRequestedAt", t."SpareDeliveredAt", t."WaitingSince"
                FROM "StationTools" t JOIN "Machines" m ON m."StationCode" = t."StationCode" AND m."Number" = 1;

                INSERT INTO "MachineTools" ("MachineCode", "PiecesSinceChange")
                SELECT m."Code", 0
                FROM "Machines" m JOIN "StationTools" t ON t."StationCode" = m."StationCode"
                WHERE m."Number" > 1;
                """);

            migrationBuilder.DropTable(
                name: "StationTools");

            // 換刀紀錄：舊的都是那一站的 1 號機。
            migrationBuilder.AddColumn<string>(
                name: "MachineCode",
                table: "ToolChanges",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql("""UPDATE "ToolChanges" SET "MachineCode" = "StationCode" || '-1';""");

            migrationBuilder.AlterColumn<string>(
                name: "MachineCode",
                table: "ToolChanges",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            // 已開始的逐件加工：在那一站的 1 號機做的。整爐工序與還在排隊的沒有機台。
            migrationBuilder.AddColumn<string>(
                name: "MachineCode",
                table: "Operations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Operations" o SET "MachineCode" = m."Code"
                FROM "Machines" m
                WHERE m."StationCode" = o."StationCode" AND m."Number" = 1 AND o."StartedAt" IS NOT NULL AND o."BatchRunId" IS NULL;
                """);

            // 送刀任務：送給那一站的 1 號機。
            migrationBuilder.AddColumn<string>(
                name: "MachineCode",
                table: "TransportTasks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "TransportTasks" t SET "MachineCode" = m."Code"
                FROM "Machines" m
                WHERE m."StationCode" = t."ToStationCode" AND m."Number" = 1 AND t."Kind" = 'ToolDelivery';
                """);

            // 缺車停機：一台機台同一時間一筆（原本是一站一筆），舊的都是 1 號機。
            migrationBuilder.DropIndex(
                name: "UX_MachineDelays_OpenStation",
                table: "MachineDelays");

            migrationBuilder.AddColumn<string>(
                name: "MachineCode",
                table: "MachineDelays",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql("""UPDATE "MachineDelays" SET "MachineCode" = "StationCode" || '-1';""");

            migrationBuilder.AlterColumn<string>(
                name: "MachineCode",
                table: "MachineDelays",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransportTasks_MachineCode",
                table: "TransportTasks",
                column: "MachineCode");

            migrationBuilder.CreateIndex(
                name: "IX_ToolChanges_MachineCode",
                table: "ToolChanges",
                column: "MachineCode");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_MachineCode_StartedAt",
                table: "Operations",
                columns: new[] { "MachineCode", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Operations_RunningMachine",
                table: "Operations",
                column: "MachineCode",
                unique: true,
                filter: "\"Status\" = 'Running'");

            migrationBuilder.CreateIndex(
                name: "IX_MachineDelays_StationCode",
                table: "MachineDelays",
                column: "StationCode");

            migrationBuilder.CreateIndex(
                name: "UX_MachineDelays_OpenMachine",
                table: "MachineDelays",
                column: "MachineCode",
                unique: true,
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_MachineDelays_Machines_MachineCode",
                table: "MachineDelays",
                column: "MachineCode",
                principalTable: "Machines",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Operations_Machines_MachineCode",
                table: "Operations",
                column: "MachineCode",
                principalTable: "Machines",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ToolChanges_Machines_MachineCode",
                table: "ToolChanges",
                column: "MachineCode",
                principalTable: "Machines",
                principalColumn: "Code",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TransportTasks_Machines_MachineCode",
                table: "TransportTasks",
                column: "MachineCode",
                principalTable: "Machines",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MachineDelays_Machines_MachineCode",
                table: "MachineDelays");

            migrationBuilder.DropForeignKey(
                name: "FK_Operations_Machines_MachineCode",
                table: "Operations");

            migrationBuilder.DropForeignKey(
                name: "FK_ToolChanges_Machines_MachineCode",
                table: "ToolChanges");

            migrationBuilder.DropForeignKey(
                name: "FK_TransportTasks_Machines_MachineCode",
                table: "TransportTasks");

            migrationBuilder.DropIndex(
                name: "IX_TransportTasks_MachineCode",
                table: "TransportTasks");

            migrationBuilder.DropIndex(
                name: "IX_ToolChanges_MachineCode",
                table: "ToolChanges");

            migrationBuilder.DropIndex(
                name: "IX_Operations_MachineCode_StartedAt",
                table: "Operations");

            migrationBuilder.DropIndex(
                name: "UX_Operations_RunningMachine",
                table: "Operations");

            migrationBuilder.DropIndex(
                name: "IX_MachineDelays_StationCode",
                table: "MachineDelays");

            migrationBuilder.DropIndex(
                name: "UX_MachineDelays_OpenMachine",
                table: "MachineDelays");

            // 回到一站一台：保留 1 號機的刀具狀態；同一站同時停著的只留一筆進行中。
            migrationBuilder.CreateTable(
                name: "StationTools",
                columns: table => new
                {
                    StationCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChangeDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ChangeStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PiecesSinceChange = table.Column<int>(type: "integer", nullable: false),
                    SpareDeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SpareRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WaitingSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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

            migrationBuilder.Sql("""
                INSERT INTO "StationTools" ("StationCode", "PiecesSinceChange", "ChangeStartedAt", "ChangeDueAt", "SpareRequestedAt", "SpareDeliveredAt", "WaitingSince")
                SELECT m."StationCode", t."PiecesSinceChange", t."ChangeStartedAt", t."ChangeDueAt", t."SpareRequestedAt", t."SpareDeliveredAt", t."WaitingSince"
                FROM "MachineTools" t JOIN "Machines" m ON m."Code" = t."MachineCode"
                WHERE m."Number" = 1;

                UPDATE "MachineDelays" d SET "EndedAt" = d."AccruedAt"
                WHERE d."EndedAt" IS NULL AND EXISTS (
                    SELECT 1 FROM "MachineDelays" o WHERE o."EndedAt" IS NULL AND o."StationCode" = d."StationCode" AND o."MachineCode" < d."MachineCode");

                UPDATE "Stations" SET "InboundCapacity" = 30, "OutboundCapacity" = 30
                WHERE "Code" IN ('rough', 'fine') AND "InboundCapacity" = 60;
                """);

            migrationBuilder.DropTable(
                name: "MachineTools");

            migrationBuilder.DropColumn(
                name: "MachineCode",
                table: "TransportTasks");

            migrationBuilder.DropColumn(
                name: "MachineCode",
                table: "ToolChanges");

            migrationBuilder.DropColumn(
                name: "MachineCode",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "MachineCode",
                table: "MachineDelays");

            migrationBuilder.DropTable(
                name: "Machines");

            migrationBuilder.CreateIndex(
                name: "UX_MachineDelays_OpenStation",
                table: "MachineDelays",
                column: "StationCode",
                unique: true,
                filter: "\"EndedAt\" IS NULL");
        }
    }
}
