using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstagramFollowCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "instagram_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    encrypted_password = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    two_factor_secret = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    session_cookies_json = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_logged_in = table.Column<bool>(type: "boolean", nullable: false),
                    is_action_blocked = table.Column<bool>(type: "boolean", nullable: false),
                    blocked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_awaiting_two_factor = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instagram_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_instagram_accounts_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "instagram_follow_campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instagram_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_handle = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    daily_rate = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                    max_total_follows = table.Column<int>(type: "integer", nullable: false),
                    scrape_batch_size = table.Column<int>(type: "integer", nullable: false, defaultValue: 100),
                    min_queued_threshold = table.Column<int>(type: "integer", nullable: false, defaultValue: 20),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    total_enqueued = table.Column<int>(type: "integer", nullable: false),
                    total_followed = table.Column<int>(type: "integer", nullable: false),
                    total_failed = table.Column<int>(type: "integer", nullable: false),
                    total_skipped = table.Column<int>(type: "integer", nullable: false),
                    last_scrape_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_follow_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instagram_follow_campaigns", x => x.id);
                    table.ForeignKey(
                        name: "fk_instagram_follow_campaigns_instagram_accounts_instagram_acc",
                        column: x => x.instagram_account_id,
                        principalTable: "instagram_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "instagram_metrics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instagram_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    profiles_scraped = table.Column<int>(type: "integer", nullable: false),
                    leads_created = table.Column<int>(type: "integer", nullable: false),
                    dm_sent = table.Column<int>(type: "integer", nullable: false),
                    dm_failed = table.Column<int>(type: "integer", nullable: false),
                    dm_replies = table.Column<int>(type: "integer", nullable: false),
                    follows_done = table.Column<int>(type: "integer", nullable: false),
                    follows_blocked = table.Column<int>(type: "integer", nullable: false),
                    action_blocks = table.Column<int>(type: "integer", nullable: false),
                    warnings = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instagram_metrics", x => x.id);
                    table.ForeignKey(
                        name: "fk_instagram_metrics_instagram_accounts_instagram_account_id",
                        column: x => x.instagram_account_id,
                        principalTable: "instagram_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "instagram_scrape_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_value = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    max_per_run = table.Column<int>(type: "integer", nullable: false),
                    assigned_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_scraped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    total_leads_created = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instagram_scrape_targets", x => x.id);
                    table.ForeignKey(
                        name: "fk_instagram_scrape_targets_instagram_accounts_assigned_accoun",
                        column: x => x.assigned_account_id,
                        principalTable: "instagram_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_instagram_scrape_targets_products_product_key",
                        column: x => x.product_key,
                        principalTable: "products",
                        principalColumn: "product_key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "instagram_structure_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_date = table.Column<DateOnly>(type: "date", nullable: false),
                    selectors_json = table.Column<string>(type: "text", nullable: false),
                    raw_html_json = table.Column<string>(type: "text", nullable: false),
                    llm_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_valid = table.Column<bool>(type: "boolean", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    instagram_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instagram_structure_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_instagram_structure_snapshots_instagram_accounts_instagram_",
                        column: x => x.instagram_account_id,
                        principalTable: "instagram_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "instagram_follow_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instagram_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_handle = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    enqueued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    executed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instagram_follow_actions", x => x.id);
                    table.ForeignKey(
                        name: "fk_instagram_follow_actions_instagram_follow_campaigns_campaig",
                        column: x => x.campaign_id,
                        principalTable: "instagram_follow_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_instagram_accounts_seller_id",
                table: "instagram_accounts",
                column: "seller_id");

            migrationBuilder.CreateIndex(
                name: "ix_instagram_follow_actions_campaign_id_status",
                table: "instagram_follow_actions",
                columns: new[] { "campaign_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_instagram_follow_actions_campaign_id_target_handle",
                table: "instagram_follow_actions",
                columns: new[] { "campaign_id", "target_handle" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_instagram_follow_actions_instagram_account_id_executed_at",
                table: "instagram_follow_actions",
                columns: new[] { "instagram_account_id", "executed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_instagram_follow_campaigns_instagram_account_id",
                table: "instagram_follow_campaigns",
                column: "instagram_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_instagram_follow_campaigns_is_active",
                table: "instagram_follow_campaigns",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_instagram_metrics_instagram_account_id_date",
                table: "instagram_metrics",
                columns: new[] { "instagram_account_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_instagram_scrape_targets_assigned_account_id",
                table: "instagram_scrape_targets",
                column: "assigned_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_instagram_scrape_targets_product_key",
                table: "instagram_scrape_targets",
                column: "product_key");

            migrationBuilder.CreateIndex(
                name: "ix_instagram_structure_snapshots_instagram_account_id",
                table: "instagram_structure_snapshots",
                column: "instagram_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_instagram_structure_snapshots_is_valid",
                table: "instagram_structure_snapshots",
                column: "is_valid");

            migrationBuilder.CreateIndex(
                name: "ix_instagram_structure_snapshots_snapshot_date",
                table: "instagram_structure_snapshots",
                column: "snapshot_date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "instagram_follow_actions");

            migrationBuilder.DropTable(
                name: "instagram_metrics");

            migrationBuilder.DropTable(
                name: "instagram_scrape_targets");

            migrationBuilder.DropTable(
                name: "instagram_structure_snapshots");

            migrationBuilder.DropTable(
                name: "instagram_follow_campaigns");

            migrationBuilder.DropTable(
                name: "instagram_accounts");
        }
    }
}
