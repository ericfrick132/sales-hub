using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IgDmCold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ig_dm_claimed_at",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ig_dm_error",
                table: "leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ig_dm_sent_at",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ig_dm_settings",
                columns: table => new
                {
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    account_username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    daily_cap = table.Column<int>(type: "integer", nullable: false),
                    min_gap_minutes = table.Column<int>(type: "integer", nullable: false),
                    max_gap_minutes = table.Column<int>(type: "integer", nullable: false),
                    from_hour = table.Column<int>(type: "integer", nullable: false),
                    to_hour = table.Column<int>(type: "integer", nullable: false),
                    template = table.Column<string>(type: "text", nullable: false),
                    paused_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paused_reason = table.Column<string>(type: "text", nullable: true),
                    next_allowed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ig_dm_settings", x => x.product_key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "ig_dm_claimed_at",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "ig_dm_error",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "ig_dm_sent_at",
                table: "leads");
        }
    }
}
