using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGoogleCalendarSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "google_calendar_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    client_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    client_secret = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    refresh_token = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    calendar_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    start_hour = table.Column<int>(type: "integer", nullable: false),
                    end_hour = table.Column<int>(type: "integer", nullable: false),
                    days = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    slot_minutes = table.Column<int>(type: "integer", nullable: false),
                    min_notice_minutes = table.Column<int>(type: "integer", nullable: false),
                    pending_state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    pending_state_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_google_calendar_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "google_calendar_settings");
        }
    }
}
