using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AudienceLeadCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "call_count",
                table: "audience_leads",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "call_notes",
                table: "audience_leads",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "call_reason",
                table: "audience_leads",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "call_status",
                table: "audience_leads",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_call_at",
                table: "audience_leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "last_call_by",
                table: "audience_leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lead_id",
                table: "audience_leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_call_at",
                table: "audience_leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "secondary_phone",
                table: "audience_leads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "secondary_phone_source",
                table: "audience_leads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audience_leads_call_status_next_call_at",
                table: "audience_leads",
                columns: new[] { "call_status", "next_call_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audience_leads_call_status_next_call_at",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "call_count",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "call_notes",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "call_reason",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "call_status",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "last_call_at",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "last_call_by",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "lead_id",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "next_call_at",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "secondary_phone",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "secondary_phone_source",
                table: "audience_leads");
        }
    }
}
