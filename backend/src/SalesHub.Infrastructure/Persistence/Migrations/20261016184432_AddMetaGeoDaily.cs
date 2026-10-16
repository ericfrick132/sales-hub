using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaGeoDaily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meta_geo_daily",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    account_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    campaign_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    campaign_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    adset_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    adset_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ad_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ad_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    country = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    region = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    reach = table.Column<long>(type: "bigint", nullable: false),
                    impressions = table.Column<long>(type: "bigint", nullable: false),
                    spend = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    clicks = table.Column<long>(type: "bigint", nullable: false),
                    unique_clicks = table.Column<long>(type: "bigint", nullable: false),
                    link_clicks = table.Column<long>(type: "bigint", nullable: false),
                    video_views3s = table.Column<long>(type: "bigint", nullable: false),
                    messaging_connections = table.Column<long>(type: "bigint", nullable: false),
                    first_replies = table.Column<long>(type: "bigint", nullable: false),
                    depth2 = table.Column<long>(type: "bigint", nullable: false),
                    depth3 = table.Column<long>(type: "bigint", nullable: false),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meta_geo_daily", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_meta_geo_daily_account_id_date",
                table: "meta_geo_daily",
                columns: new[] { "account_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_meta_geo_daily_date_ad_id_region",
                table: "meta_geo_daily",
                columns: new[] { "date", "ad_id", "region" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_meta_geo_daily_product_key",
                table: "meta_geo_daily",
                column: "product_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meta_geo_daily");
        }
    }
}
