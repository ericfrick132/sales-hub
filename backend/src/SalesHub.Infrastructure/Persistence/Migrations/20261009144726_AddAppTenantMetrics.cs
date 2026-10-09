using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppTenantMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_metrics_daily",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    mrr_usd = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    active = table.Column<int>(type: "integer", nullable: false),
                    trial = table.Column<int>(type: "integer", nullable: false),
                    past_due = table.Column<int>(type: "integer", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_metrics_daily", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    plan = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    monthly_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_tenants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_app_metrics_daily_product_key_date",
                table: "app_metrics_daily",
                columns: new[] { "product_key", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_tenants_product_key_external_id",
                table: "app_tenants",
                columns: new[] { "product_key", "external_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_metrics_daily");

            migrationBuilder.DropTable(
                name: "app_tenants");
        }
    }
}
