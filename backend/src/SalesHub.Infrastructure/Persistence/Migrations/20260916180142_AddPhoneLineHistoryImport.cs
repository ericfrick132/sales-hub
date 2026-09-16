using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhoneLineHistoryImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "history_import_passes",
                table: "evolution_instances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "history_import_started_at",
                table: "evolution_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "history_imported_at",
                table: "evolution_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "history_imported_messages",
                table: "evolution_instances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "import_history",
                table: "evolution_instances",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "history_import_passes",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "history_import_started_at",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "history_imported_at",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "history_imported_messages",
                table: "evolution_instances");

            migrationBuilder.DropColumn(
                name: "import_history",
                table: "evolution_instances");
        }
    }
}
