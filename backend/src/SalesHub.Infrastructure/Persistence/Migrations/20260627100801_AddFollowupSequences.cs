using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFollowupSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "followup_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    trigger = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    external_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    step_index = table.Column<int>(type: "integer", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_followup_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "followup_sequences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    trigger = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_followup_sequences", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_followup_events_product_key_created_at",
                table: "followup_events",
                columns: new[] { "product_key", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_followup_events_product_key_event_type",
                table: "followup_events",
                columns: new[] { "product_key", "event_type" });

            migrationBuilder.CreateIndex(
                name: "ix_followup_sequences_product_key_trigger",
                table: "followup_sequences",
                columns: new[] { "product_key", "trigger" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "followup_events");

            migrationBuilder.DropTable(
                name: "followup_sequences");
        }
    }
}
