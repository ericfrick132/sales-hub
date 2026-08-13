using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCrmBoardIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_leads_status_next_action_at_updated_at",
                table: "leads",
                columns: new[] { "status", "next_action_at", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_leads_status_next_action_at_updated_at",
                table: "leads");
        }
    }
}
