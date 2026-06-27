using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBacklogMessagesToFollowupSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "backlog_cold_after_days",
                table: "followup_sequences",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "backlog_cold_message",
                table: "followup_sequences",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "backlog_warm_message",
                table: "followup_sequences",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "backlog_cold_after_days",
                table: "followup_sequences");

            migrationBuilder.DropColumn(
                name: "backlog_cold_message",
                table: "followup_sequences");

            migrationBuilder.DropColumn(
                name: "backlog_warm_message",
                table: "followup_sequences");
        }
    }
}
