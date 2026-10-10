using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenFleet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TransportRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Resolution",
                table: "TransportTasks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolvedAt",
                table: "TransportTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MissingSince",
                table: "Assignments",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Resolution",
                table: "TransportTasks");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "TransportTasks");

            migrationBuilder.DropColumn(
                name: "MissingSince",
                table: "Assignments");
        }
    }
}
