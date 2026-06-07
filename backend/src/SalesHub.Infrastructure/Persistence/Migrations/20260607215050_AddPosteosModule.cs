using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPosteosModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "posting_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    brand_colors_json = table.Column<string>(type: "jsonb", nullable: false),
                    brand_fonts = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    brand_logo_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    brand_voice = table.Column<string>(type: "text", nullable: false),
                    brand_guidelines = table.Column<string>(type: "text", nullable: false),
                    target_audience = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_pillars = table.Column<List<string>>(type: "text[]", nullable: false),
                    buffer_channels_json = table.Column<string>(type: "jsonb", nullable: false),
                    post_hours = table.Column<List<int>>(type: "integer[]", nullable: false),
                    posts_per_day = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posting_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "social_posts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    buffer_channel_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    asset_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    content_pillar = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    concept = table.Column<string>(type: "text", nullable: false),
                    prompt = table.Column<string>(type: "text", nullable: false),
                    caption = table.Column<string>(type: "text", nullable: false),
                    hashtags = table.Column<List<string>>(type: "text[]", nullable: false),
                    asset_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    thumbnail_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    buffer_post_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    generation_model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    raw_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_posts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_posting_profiles_product_key",
                table: "posting_profiles",
                column: "product_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_social_posts_created_at",
                table: "social_posts",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_social_posts_product_key_status",
                table: "social_posts",
                columns: new[] { "product_key", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_social_posts_status",
                table: "social_posts",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "posting_profiles");

            migrationBuilder.DropTable(
                name: "social_posts");
        }
    }
}
