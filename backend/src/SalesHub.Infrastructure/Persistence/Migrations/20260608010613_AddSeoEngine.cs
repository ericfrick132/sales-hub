using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSeoEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "seo_sites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    domain = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    sector = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    audience = table.Column<string>(type: "text", nullable: false),
                    product_summary = table.Column<string>(type: "text", nullable: false),
                    brand_voice = table.Column<string>(type: "text", nullable: false),
                    target_countries = table.Column<List<string>>(type: "text[]", nullable: false),
                    blog_base_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    auto_publish = table.Column<bool>(type: "boolean", nullable: false),
                    weekly_target = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seo_sites", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "seo_keywords",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    cluster = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    intent = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    volume = table.Column<int>(type: "integer", nullable: true),
                    difficulty = table.Column<int>(type: "integer", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seo_keywords", x => x.id);
                    table.ForeignKey(
                        name: "fk_seo_keywords_seo_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "seo_sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "seo_articles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    keyword_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_keyword = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    slug = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    meta_description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    body_markdown = table.Column<string>(type: "text", nullable: false),
                    faq_json = table.Column<string>(type: "jsonb", nullable: false),
                    json_ld = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    seo_score = table.Column<int>(type: "integer", nullable: true),
                    optimization_notes = table.Column<string>(type: "text", nullable: false),
                    word_count = table.Column<int>(type: "integer", nullable: false),
                    generated_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seo_articles", x => x.id);
                    table.ForeignKey(
                        name: "fk_seo_articles_seo_keywords_keyword_id",
                        column: x => x.keyword_id,
                        principalTable: "seo_keywords",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_seo_articles_seo_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "seo_sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_seo_articles_keyword_id",
                table: "seo_articles",
                column: "keyword_id");

            migrationBuilder.CreateIndex(
                name: "ix_seo_articles_site_id_status",
                table: "seo_articles",
                columns: new[] { "site_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_seo_keywords_site_id_term",
                table: "seo_keywords",
                columns: new[] { "site_id", "term" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seo_keywords_status",
                table: "seo_keywords",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_seo_sites_domain",
                table: "seo_sites",
                column: "domain");

            migrationBuilder.CreateIndex(
                name: "ix_seo_sites_product_key",
                table: "seo_sites",
                column: "product_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "seo_articles");

            migrationBuilder.DropTable(
                name: "seo_keywords");

            migrationBuilder.DropTable(
                name: "seo_sites");
        }
    }
}
