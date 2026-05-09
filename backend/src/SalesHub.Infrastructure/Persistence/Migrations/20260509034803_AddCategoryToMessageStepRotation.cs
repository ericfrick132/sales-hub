using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryToMessageStepRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_message_step_rotations",
                table: "message_step_rotations");

            migrationBuilder.AddColumn<string>(
                name: "category_cadences",
                table: "products",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            // Normaliza filas que pudieran quedar con default vacío de un
            // intento previo de la migration ("" → {}). Idempotente: si ya
            // están como '[]' no hace nada.
            migrationBuilder.Sql("UPDATE products SET category_cadences = '[]'::jsonb WHERE category_cadences::text !~ '^\\['");

            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "message_step_rotations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "pk_message_step_rotations",
                table: "message_step_rotations",
                columns: new[] { "product_id", "category", "step_index" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_message_step_rotations",
                table: "message_step_rotations");

            migrationBuilder.DropColumn(
                name: "category_cadences",
                table: "products");

            migrationBuilder.DropColumn(
                name: "category",
                table: "message_step_rotations");

            migrationBuilder.AddPrimaryKey(
                name: "pk_message_step_rotations",
                table: "message_step_rotations",
                columns: new[] { "product_id", "step_index" });
        }
    }
}
