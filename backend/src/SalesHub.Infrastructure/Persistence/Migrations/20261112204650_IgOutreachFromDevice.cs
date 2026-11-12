using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IgOutreachFromDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "account_verified_on",
                table: "ig_dm_settings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cold_ramp",
                table: "ig_dm_settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "consecutive_failures",
                table: "ig_dm_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "ig_dm_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "follower_exclude",
                table: "ig_dm_settings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "follower_ramp",
                table: "ig_dm_settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "follower_scan_at",
                table: "ig_dm_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "follower_scan_requested",
                table: "ig_dm_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "follower_scan_swipes",
                table: "ig_dm_settings",
                type: "integer",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddColumn<string>(
                name: "follower_template",
                table: "ig_dm_settings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "followers_enabled",
                table: "ig_dm_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ramp_started_on",
                table: "ig_dm_settings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sender",
                table: "ig_dm_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "extension");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "throttled_until",
                table: "ig_dm_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ig_device_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    follower_id = table.Column<Guid>(type: "uuid", nullable: true),
                    handle = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    text = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    screen = table.Column<string>(type: "text", nullable: true),
                    collected = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ig_device_tasks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ig_followers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dm_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    skipped_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ig_followers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ig_dm_settings_device_id",
                table: "ig_dm_settings",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_ig_device_tasks_device_id_status",
                table: "ig_device_tasks",
                columns: new[] { "device_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_ig_device_tasks_product_key_created_at",
                table: "ig_device_tasks",
                columns: new[] { "product_key", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ig_followers_product_key_dm_sent_at",
                table: "ig_followers",
                columns: new[] { "product_key", "dm_sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ig_followers_product_key_username",
                table: "ig_followers",
                columns: new[] { "product_key", "username" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ig_device_tasks");

            migrationBuilder.DropTable(
                name: "ig_followers");

            migrationBuilder.DropIndex(
                name: "ix_ig_dm_settings_device_id",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "account_verified_on",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "cold_ramp",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "consecutive_failures",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "follower_exclude",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "follower_ramp",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "follower_scan_at",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "follower_scan_requested",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "follower_scan_swipes",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "follower_template",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "followers_enabled",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "ramp_started_on",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "sender",
                table: "ig_dm_settings");

            migrationBuilder.DropColumn(
                name: "throttled_until",
                table: "ig_dm_settings");
        }
    }
}
