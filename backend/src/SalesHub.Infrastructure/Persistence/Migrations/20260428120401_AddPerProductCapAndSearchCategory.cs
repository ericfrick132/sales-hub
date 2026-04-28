using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerProductCapAndSearchCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "search_category",
                table: "leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "google_places_daily_lead_cap",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 60);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "search_category",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "google_places_daily_lead_cap",
                table: "products");
        }
    }
}
