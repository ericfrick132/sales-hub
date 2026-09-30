using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIntentSimulation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reply_by_product",
                table: "reply_intents",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "intent_action",
                table: "conversation_messages",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "intent_confident",
                table: "conversation_messages",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "intent_simulated_reply",
                table: "conversation_messages",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reply_by_product",
                table: "reply_intents");

            migrationBuilder.DropColumn(
                name: "intent_action",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "intent_confident",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "intent_simulated_reply",
                table: "conversation_messages");
        }
    }
}
