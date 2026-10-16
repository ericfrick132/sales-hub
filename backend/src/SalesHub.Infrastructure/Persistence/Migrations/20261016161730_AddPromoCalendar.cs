using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPromoCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "promo_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    apps = table.Column<List<string>>(type: "text[]", nullable: false),
                    discount_percent = table.Column<int>(type: "integer", nullable: true),
                    duration_months = table.Column<int>(type: "integer", nullable: true),
                    coupon_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    lead_days = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    confidence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    image_source_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    image_credit = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    image_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promo_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "promo_campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    promo_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    coupon_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    discount_percent = table.Column<int>(type: "integer", nullable: false),
                    duration_months = table.Column<int>(type: "integer", nullable: false),
                    headline = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    subheadline = table.Column<string>(type: "text", nullable: false),
                    bullets = table.Column<List<string>>(type: "text[]", nullable: false),
                    landing_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    tracked_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    launched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promo_campaigns", x => x.id);
                    table.ForeignKey(
                        name: "fk_promo_campaigns_promo_events_promo_event_id",
                        column: x => x.promo_event_id,
                        principalTable: "promo_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_promo_campaigns_product_key_status",
                table: "promo_campaigns",
                columns: new[] { "product_key", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_promo_campaigns_promo_event_id_product_key",
                table: "promo_campaigns",
                columns: new[] { "promo_event_id", "product_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_promo_events_date",
                table: "promo_events",
                column: "date");

            migrationBuilder.CreateIndex(
                name: "ix_promo_events_slug",
                table: "promo_events",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promo_campaigns");

            migrationBuilder.DropTable(
                name: "promo_events");
        }
    }
}
