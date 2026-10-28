using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClaudeTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "claude_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reply_to = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    prompt = table.Column<string>(type: "text", nullable: false),
                    resume_session_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    session_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    result = table.Column<string>(type: "text", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claude_tasks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_claude_tasks_status_created_at",
                table: "claude_tasks",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "claude_tasks");
        }
    }
}
