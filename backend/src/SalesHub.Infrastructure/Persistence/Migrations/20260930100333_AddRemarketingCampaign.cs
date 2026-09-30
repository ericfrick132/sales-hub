using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRemarketingCampaign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "remarketing_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    personalized_with_ai = table.Column<bool>(type: "boolean", nullable: false),
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enqueued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    replied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remarketing_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_remarketing_attempts_leads_lead_id",
                        column: x => x.lead_id,
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "remarketing_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    per_line_per_day = table.Column<int>(type: "integer", nullable: false),
                    min_idle_days = table.Column<int>(type: "integer", nullable: false),
                    max_idle_days = table.Column<int>(type: "integer", nullable: true),
                    send_hour_start = table.Column<int>(type: "integer", nullable: false),
                    send_hour_end = table.Column<int>(type: "integer", nullable: false),
                    sender_seller_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'"),
                    product_keys = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    personalize_with_ai = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remarketing_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_remarketing_attempts_lead_id",
                table: "remarketing_attempts",
                column: "lead_id");

            migrationBuilder.CreateIndex(
                name: "ix_remarketing_attempts_seller_id_enqueued_at",
                table: "remarketing_attempts",
                columns: new[] { "seller_id", "enqueued_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "remarketing_attempts");

            migrationBuilder.DropTable(
                name: "remarketing_settings");
        }
    }
}
