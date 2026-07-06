using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFollowCampaignActiveHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "active_hour_end",
                table: "instagram_follow_campaigns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "active_hour_start",
                table: "instagram_follow_campaigns",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "active_hour_end",
                table: "instagram_follow_campaigns");

            migrationBuilder.DropColumn(
                name: "active_hour_start",
                table: "instagram_follow_campaigns");
        }
    }
}
