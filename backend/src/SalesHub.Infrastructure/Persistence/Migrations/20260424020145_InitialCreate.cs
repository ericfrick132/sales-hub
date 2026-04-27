using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "apify_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    input_json = table.Column<string>(type: "jsonb", nullable: true),
                    apify_run_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    items_count = table.Column<int>(type: "integer", nullable: false),
                    leads_created = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_apify_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cities_queue",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    country = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    province = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    city = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    population_bucket = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cities_queue", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "competitors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    vertical = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    followers_count = table.Column<int>(type: "integer", nullable: true),
                    following_count = table.Column<int>(type: "integer", nullable: true),
                    posts_count = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    raw_profile_json = table.Column<string>(type: "jsonb", nullable: true),
                    last_scraped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_competitors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    country = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    country_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    region_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    phone_prefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    categories = table.Column<List<string>>(type: "text[]", nullable: false),
                    message_template = table.Column<string>(type: "text", nullable: false),
                    checkout_url = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    price_display = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    daily_limit = table.Column<int>(type: "integer", nullable: false),
                    trigger_hours = table.Column<List<int>>(type: "integer[]", nullable: false),
                    requires_assisted_sale = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.UniqueConstraint("ak_products_product_key", x => x.product_key);
                });

            migrationBuilder.CreateTable(
                name: "scrape_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    country = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    city = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    source = table.Column<int>(type: "integer", nullable: false),
                    results_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scrape_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sellers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    google_subject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    role = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    whatsapp_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    verticals_whitelist = table.Column<List<string>>(type: "text[]", nullable: false),
                    send_mode = table.Column<int>(type: "integer", nullable: false),
                    daily_cap = table.Column<int>(type: "integer", nullable: false),
                    daily_variance_pct = table.Column<int>(type: "integer", nullable: false),
                    warmup_days = table.Column<int>(type: "integer", nullable: false),
                    warmup_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    active_hours_start = table.Column<int>(type: "integer", nullable: false),
                    active_hours_end = table.Column<int>(type: "integer", nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    delay_min_seconds = table.Column<int>(type: "integer", nullable: false),
                    delay_max_seconds = table.Column<int>(type: "integer", nullable: false),
                    burst_size = table.Column<int>(type: "integer", nullable: false),
                    burst_pause_min_seconds = table.Column<int>(type: "integer", nullable: false),
                    burst_pause_max_seconds = table.Column<int>(type: "integer", nullable: false),
                    pre_send_typing_min_seconds = table.Column<int>(type: "integer", nullable: false),
                    pre_send_typing_max_seconds = table.Column<int>(type: "integer", nullable: false),
                    read_incoming_first = table.Column<bool>(type: "boolean", nullable: false),
                    skip_day_probability_pct = table.Column<int>(type: "integer", nullable: false),
                    typo_probability_pct = table.Column<int>(type: "integer", nullable: false),
                    sending_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sellers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "competitor_posts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    competitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_post_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    post_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    caption = table.Column<string>(type: "text", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    likes = table.Column<int>(type: "integer", nullable: false),
                    comments_count = table.Column<int>(type: "integer", nullable: false),
                    hashtags = table.Column<List<string>>(type: "text[]", nullable: false),
                    raw_json = table.Column<string>(type: "jsonb", nullable: true),
                    scraped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_competitor_posts", x => x.id);
                    table.ForeignKey(
                        name: "fk_competitor_posts_competitors_competitor_id",
                        column: x => x.competitor_id,
                        principalTable: "competitors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evolution_instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    connected_phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    last_status_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disconnected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_qr_code_base64 = table.Column<string>(type: "text", nullable: true),
                    qr_code_generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evolution_instances", x => x.id);
                    table.ForeignKey(
                        name: "fk_evolution_instances_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    external_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    place_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    address = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    city = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    province = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    country = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    raw_phone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    whatsapp_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    whatsapp_jid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    whatsapp_validated = table.Column<bool>(type: "boolean", nullable: false),
                    website = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    instagram_handle = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    facebook_url = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    rating = table.Column<double>(type: "double precision", nullable: true),
                    total_reviews = table.Column<int>(type: "integer", nullable: true),
                    business_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    types = table.Column<List<string>>(type: "text[]", nullable: false),
                    search_query = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    raw_data_json = table.Column<string>(type: "jsonb", nullable: true),
                    score = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    rendered_message = table.Column<string>(type: "text", nullable: true),
                    whatsapp_link = table.Column<string>(type: "text", nullable: true),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_reply_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_leads", x => x.id);
                    table.ForeignKey(
                        name: "fk_leads_products_product_key",
                        column: x => x.product_key,
                        principalTable: "products",
                        principalColumn: "product_key",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_leads_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "seller_daily_stats",
                columns: table => new
                {
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    planned_cap = table.Column<int>(type: "integer", nullable: false),
                    messages_sent = table.Column<int>(type: "integer", nullable: false),
                    messages_failed = table.Column<int>(type: "integer", nullable: false),
                    replies = table.Column<int>(type: "integer", nullable: false),
                    demos = table.Column<int>(type: "integer", nullable: false),
                    closed = table.Column<int>(type: "integer", nullable: false),
                    lost = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seller_daily_stats", x => new { x.seller_id, x.date });
                    table.ForeignKey(
                        name: "fk_seller_daily_stats_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "competitor_comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_handle = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    text = table.Column<string>(type: "text", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_negative = table.Column<bool>(type: "boolean", nullable: false),
                    raw_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_competitor_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_competitor_comments_competitor_posts_post_id",
                        column: x => x.post_id,
                        principalTable: "competitor_posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evolution_instance = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    whatsapp_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_outbox", x => x.id);
                    table.ForeignKey(
                        name: "fk_message_outbox_leads_lead_id",
                        column: x => x.lead_id,
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_message_outbox_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_apify_runs_started_at",
                table: "apify_runs",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_cities_queue_country_province_city",
                table: "cities_queue",
                columns: new[] { "country", "province", "city" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_competitor_comments_is_negative",
                table: "competitor_comments",
                column: "is_negative");

            migrationBuilder.CreateIndex(
                name: "ix_competitor_comments_post_id",
                table: "competitor_comments",
                column: "post_id");

            migrationBuilder.CreateIndex(
                name: "ix_competitor_posts_competitor_id_external_post_id",
                table: "competitor_posts",
                columns: new[] { "competitor_id", "external_post_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_competitors_platform_handle",
                table: "competitors",
                columns: new[] { "platform", "handle" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_evolution_instances_instance_name",
                table: "evolution_instances",
                column: "instance_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_evolution_instances_seller_id",
                table: "evolution_instances",
                column: "seller_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_leads_created_at",
                table: "leads",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_leads_product_key_place_id",
                table: "leads",
                columns: new[] { "product_key", "place_id" },
                filter: "place_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_leads_product_key_whatsapp_phone",
                table: "leads",
                columns: new[] { "product_key", "whatsapp_phone" },
                unique: true,
                filter: "whatsapp_phone IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_leads_seller_id_status",
                table: "leads",
                columns: new[] { "seller_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_leads_status",
                table: "leads",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_message_outbox_lead_id",
                table: "message_outbox",
                column: "lead_id");

            migrationBuilder.CreateIndex(
                name: "ix_message_outbox_seller_id_status_scheduled_at",
                table: "message_outbox",
                columns: new[] { "seller_id", "status", "scheduled_at" });

            migrationBuilder.CreateIndex(
                name: "ix_message_outbox_status_scheduled_at",
                table: "message_outbox",
                columns: new[] { "status", "scheduled_at" });

            migrationBuilder.CreateIndex(
                name: "ix_products_product_key",
                table: "products",
                column: "product_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scrape_log_product_key_city_category_run_at",
                table: "scrape_log",
                columns: new[] { "product_key", "city", "category", "run_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sellers_email",
                table: "sellers",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sellers_seller_key",
                table: "sellers",
                column: "seller_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "apify_runs");

            migrationBuilder.DropTable(
                name: "cities_queue");

            migrationBuilder.DropTable(
                name: "competitor_comments");

            migrationBuilder.DropTable(
                name: "evolution_instances");

            migrationBuilder.DropTable(
                name: "message_outbox");

            migrationBuilder.DropTable(
                name: "scrape_log");

            migrationBuilder.DropTable(
                name: "seller_daily_stats");

            migrationBuilder.DropTable(
                name: "competitor_posts");

            migrationBuilder.DropTable(
                name: "leads");

            migrationBuilder.DropTable(
                name: "competitors");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "sellers");
        }
    }
}
