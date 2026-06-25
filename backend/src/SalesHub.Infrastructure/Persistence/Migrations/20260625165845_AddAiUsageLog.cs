using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsageLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_usage_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    feature = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    cache_read_tokens = table.Column<int>(type: "integer", nullable: false),
                    cache_creation_tokens = table.Column<int>(type: "integer", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(14,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage_logs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_created_at",
                table: "ai_usage_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_created_at_feature",
                table: "ai_usage_logs",
                columns: new[] { "created_at", "feature" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_usage_logs");
        }
    }
}
