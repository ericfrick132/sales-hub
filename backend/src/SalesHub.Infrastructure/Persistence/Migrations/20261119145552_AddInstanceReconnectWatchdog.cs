using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceReconnectWatchdog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "down_alerted_at",
                table: "evolution_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_reconnect_attempt_at",
                table: "evolution_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_seen_open_at",
                table: "evolution_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "needs_qr_since",
                table: "evolution_instances",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "down_alerted_at",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "last_reconnect_attempt_at",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "last_seen_open_at",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "needs_qr_since",
                table: "evolution_instances");
        }
    }
}
