using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportBotAndTone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El bloque TÉCNICA de gymhero salió del system prompt hardcodeado
            // (AiSuggestionService.BuildSystemPrompt) y pasa a data: se apendea al
            // AiSalesPlaybook del producto, donde ya era editable por UI. Idempotente.
            migrationBuilder.Sql(@"
UPDATE products SET ai_sales_playbook = TRIM(BOTH E'\n' FROM ai_sales_playbook || E'\n\nTECNICA (lo que MEJOR convierte en este producto, seguilo):
- si el lead dice que YA TIENE un sistema, NO te rindas: repregunta si ese sistema le cobra automatico por mercado pago o si sigue persiguiendo pagos a mano, y si ve en recepcion quien esta al dia. ahi se abre la venta. una objecion tipo ""ya tengo"" es una oportunidad, no un no.
- si dice que NO tiene morosos, encaralo como plata que se le escapa sin darse cuenta (cobra dos veces el mismo mes, se olvida de alguien), no como perseguir morosos.
- si dice que no es el dueno, pedile el contacto o mail del dueno para mandarle la info directa.
- ante un ""ya tengo otro"", ofrece que le migras los datos gratis y que se configura en 1 dia.
- si el lead dice que NO de forma firme, responde UNA vez corto y cordial y cerra. NUNCA insistas ni mandes otro mensaje: insistir sobre un no quema la marca.
- nada de charla de cortesia interminable: si no hay avance comercial en 1 o 2 mensajes, cerra amable.')
WHERE product_key = 'gymhero' AND ai_sales_playbook NOT LIKE '%TECNICA (lo que MEJOR convierte%';");

            migrationBuilder.CreateTable(
                name: "product_faqs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    question = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    keywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    times_used = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_faqs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_knowledge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    source_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_knowledge", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "support_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    bot_turns = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_bot_reply_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    escalated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_cases", x => x.id);
                    table.ForeignKey(
                        name: "fk_support_cases_leads_lead_id",
                        column: x => x.lead_id,
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tone_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tone_profiles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_product_faqs_product_key",
                table: "product_faqs",
                column: "product_key");

            migrationBuilder.CreateIndex(
                name: "ix_product_knowledge_product_key",
                table: "product_knowledge",
                column: "product_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_support_cases_lead_id_status",
                table: "support_cases",
                columns: new[] { "lead_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_tone_profiles_scope",
                table: "tone_profiles",
                column: "scope",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_faqs");

            migrationBuilder.DropTable(
                name: "product_knowledge");

            migrationBuilder.DropTable(
                name: "support_cases");

            migrationBuilder.DropTable(
                name: "tone_profiles");
        }
    }
}
