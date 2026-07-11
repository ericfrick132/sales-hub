using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceCadences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_cadences",
                table: "products",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            // Igual que category_cadences: si un intento previo dejó valores
            // no-array, normalizarlos. Idempotente.
            migrationBuilder.Sql("UPDATE products SET source_cadences = '[]'::jsonb WHERE source_cadences::text !~ '^\\['");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_cadences",
                table: "products");
        }
    }
}
