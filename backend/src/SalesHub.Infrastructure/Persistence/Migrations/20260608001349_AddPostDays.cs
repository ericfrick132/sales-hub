using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPostDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // default '{}' para que las filas existentes (perfiles ya seedeados)
            // no queden NULL en una columna NOT NULL.
            migrationBuilder.AddColumn<List<int>>(
                name: "post_days",
                table: "posting_profiles",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "post_days",
                table: "posting_profiles");
        }
    }
}
