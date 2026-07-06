using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInspirationItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "inspiration_item_id",
                table: "social_posts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "inspiration_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    topic = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    source_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    mime_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    image_content = table.Column<byte[]>(type: "bytea", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    pending_topic = table.Column<bool>(type: "boolean", nullable: false),
                    times_used = table.Column<int>(type: "integer", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inspiration_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inspiration_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    instance_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    master_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inspiration_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inspiration_items_created_at",
                table: "inspiration_items",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_inspiration_items_pending_topic",
                table: "inspiration_items",
                column: "pending_topic");

            migrationBuilder.CreateIndex(
                name: "ix_inspiration_items_product_key",
                table: "inspiration_items",
                column: "product_key");

            migrationBuilder.CreateIndex(
                name: "ix_inspiration_items_topic",
                table: "inspiration_items",
                column: "topic");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inspiration_items");

            migrationBuilder.DropTable(
                name: "inspiration_settings");

            migrationBuilder.DropColumn(
                name: "inspiration_item_id",
                table: "social_posts");
        }
    }
}
