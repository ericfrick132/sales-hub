using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadInstagramScopedId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "instagram_scoped_id",
                table: "leads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_leads_instagram_scoped_id",
                table: "leads",
                column: "instagram_scoped_id",
                filter: "instagram_scoped_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_leads_instagram_scoped_id",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "instagram_scoped_id",
                table: "leads");
        }
    }
}
