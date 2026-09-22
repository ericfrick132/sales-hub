using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoryImportProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "history_import_done_chats",
                table: "evolution_instances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "history_import_prospects",
                table: "evolution_instances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "history_import_total_chats",
                table: "evolution_instances",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "history_import_done_chats",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "history_import_prospects",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "history_import_total_chats",
                table: "evolution_instances");
        }
    }
}
