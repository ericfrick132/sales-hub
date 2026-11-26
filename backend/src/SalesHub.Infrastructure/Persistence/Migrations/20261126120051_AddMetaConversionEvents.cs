using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaConversionEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meta_conversion_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_test = table.Column<bool>(type: "boolean", nullable: false),
                    event_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_id = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    action_source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    fallback_used = table.Column<bool>(type: "boolean", nullable: false),
                    first_http_status = table.Column<int>(type: "integer", nullable: true),
                    first_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    match_keys = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    fb_trace_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meta_conversion_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_meta_conversion_events_created_at",
                table: "meta_conversion_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_meta_conversion_events_lead_id_stage_is_test",
                table: "meta_conversion_events",
                columns: new[] { "lead_id", "stage", "is_test" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meta_conversion_events");
        }
    }
}
