using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundLinesAndProspectScore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "booking_platform",
                table: "leads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone_origin",
                table: "leads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "prospect_enriched_at",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prospect_rubro",
                table: "leads",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "prospect_score",
                table: "leads",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prospect_score_detail",
                table: "leads",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "prospect_scored_at",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "outbound_cap",
                table: "evolution_instances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "outbound_min_score",
                table: "evolution_instances",
                type: "integer",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "outbound_paused_at",
                table: "evolution_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "outbound_rubros",
                table: "evolution_instances",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "outbound_sender_name",
                table: "evolution_instances",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outbound_template",
                table: "evolution_instances",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "outbound_contacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_contacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_outbound_contacts_leads_lead_id",
                        column: x => x.lead_id,
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_leads_product_key_prospect_score",
                table: "leads",
                columns: new[] { "product_key", "prospect_score" },
                filter: "prospect_score IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_contacts_instance_name",
                table: "outbound_contacts",
                column: "instance_name");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_contacts_lead_id",
                table: "outbound_contacts",
                column: "lead_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbound_contacts");

            migrationBuilder.DropIndex(
                name: "ix_leads_product_key_prospect_score",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "booking_platform",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "phone_origin",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "prospect_enriched_at",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "prospect_rubro",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "prospect_score",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "prospect_score_detail",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "prospect_scored_at",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "outbound_cap",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "outbound_min_score",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "outbound_paused_at",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "outbound_rubros",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "outbound_sender_name",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "outbound_template",
                table: "evolution_instances");
        }
    }
}
