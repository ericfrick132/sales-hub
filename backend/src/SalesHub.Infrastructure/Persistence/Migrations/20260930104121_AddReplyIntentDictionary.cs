using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReplyIntentDictionary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "intent_key",
                table: "conversation_messages",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reply_intents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    pattern = table.Column<string>(type: "text", nullable: false),
                    max_words = table.Column<int>(type: "integer", nullable: true),
                    action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reply = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    examples = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    auto_reply = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reply_intents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversation_messages_intent_key",
                table: "conversation_messages",
                column: "intent_key");

            migrationBuilder.CreateIndex(
                name: "ix_reply_intents_key",
                table: "reply_intents",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reply_intents");

            migrationBuilder.DropIndex(
                name: "ix_conversation_messages_intent_key",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "intent_key",
                table: "conversation_messages");
        }
    }
}
