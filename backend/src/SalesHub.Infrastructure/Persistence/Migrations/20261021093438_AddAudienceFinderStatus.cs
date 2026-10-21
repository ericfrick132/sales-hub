using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudienceFinderStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "finder_checked_at",
                table: "audience_leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "finder_status",
                table: "audience_leads",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audience_leads_updated_at",
                table: "audience_leads",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audience_leads_updated_at",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "finder_checked_at",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "finder_status",
                table: "audience_leads");
        }
    }
}
