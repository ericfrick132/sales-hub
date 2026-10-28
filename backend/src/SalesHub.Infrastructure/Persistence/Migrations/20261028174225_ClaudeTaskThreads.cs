using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClaudeTaskThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audio_message_id",
                table: "claude_tasks",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "code",
                table: "claude_tasks",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "parent_task_id",
                table: "claude_tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_claude_tasks_audio_message_id",
                table: "claude_tasks",
                column: "audio_message_id");

            migrationBuilder.CreateIndex(
                name: "ix_claude_tasks_reply_to_code",
                table: "claude_tasks",
                columns: new[] { "reply_to", "code" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_claude_tasks_audio_message_id",
                table: "claude_tasks");

            migrationBuilder.DropIndex(
                name: "ix_claude_tasks_reply_to_code",
                table: "claude_tasks");

            migrationBuilder.DropColumn(
                name: "audio_message_id",
                table: "claude_tasks");

            migrationBuilder.DropColumn(
                name: "code",
                table: "claude_tasks");

            migrationBuilder.DropColumn(
                name: "parent_task_id",
                table: "claude_tasks");
        }
    }
}
