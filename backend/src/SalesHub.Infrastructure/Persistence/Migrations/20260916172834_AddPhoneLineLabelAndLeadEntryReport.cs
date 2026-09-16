using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhoneLineLabelAndLeadEntryReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_evolution_instances_product_key",
                table: "evolution_instances");

            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "evolution_instances",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lead_entry_report_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    recipient_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    send_hour = table.Column<int>(type: "integer", nullable: false),
                    send_instance_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    last_reported_day = table.Column<DateOnly>(type: "date", nullable: true),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_entry_report_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_evolution_instances_product_key",
                table: "evolution_instances",
                column: "product_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_entry_report_settings");

            migrationBuilder.DropIndex(
                name: "ix_evolution_instances_product_key",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "label",
                table: "evolution_instances");

            migrationBuilder.CreateIndex(
                name: "ix_evolution_instances_product_key",
                table: "evolution_instances",
                column: "product_key",
                unique: true);
        }
    }
}
