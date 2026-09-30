using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIntentFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "intent_why",
                table: "conversation_messages",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "intent_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    intent_action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    simulated_reply = table.Column<string>(type: "text", nullable: true),
                    verdict = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    correct_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    correct_action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    better_reply = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_by_seller_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intent_feedback", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_intent_feedback_lead_id",
                table: "intent_feedback",
                column: "lead_id");

            migrationBuilder.CreateIndex(
                name: "ix_intent_feedback_message_id",
                table: "intent_feedback",
                column: "message_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "intent_feedback");

            migrationBuilder.DropColumn(
                name: "intent_why",
                table: "conversation_messages");
        }
    }
}
