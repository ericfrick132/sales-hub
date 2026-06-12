using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoPilotAndNudges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_pilot",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_nudge_at",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "nudge_count",
                table: "leads",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_pilot",
                table: "products");

            migrationBuilder.DropColumn(
                name: "last_nudge_at",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "nudge_count",
                table: "leads");
        }
    }
}
