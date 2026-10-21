using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudienceFinderLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "finder_log",
                table: "audience_leads",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audience_leads_finder_checked_at",
                table: "audience_leads",
                column: "finder_checked_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audience_leads_finder_checked_at",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "finder_log",
                table: "audience_leads");
        }
    }
}
