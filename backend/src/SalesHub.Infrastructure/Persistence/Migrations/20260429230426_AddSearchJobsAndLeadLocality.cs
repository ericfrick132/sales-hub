using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchJobsAndLeadLocality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "locality_gid2",
                table: "leads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "search_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    locality_gid2 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    query = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    raw_items = table.Column<int>(type: "integer", nullable: false),
                    leads_created = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_search_jobs_localities_locality_gid2",
                        column: x => x.locality_gid2,
                        principalTable: "localities",
                        principalColumn: "gid2",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_search_jobs_products_product_key",
                        column: x => x.product_key,
                        principalTable: "products",
                        principalColumn: "product_key",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_search_jobs_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_leads_locality_gid2",
                table: "leads",
                column: "locality_gid2");

            migrationBuilder.CreateIndex(
                name: "ix_leads_product_key_locality_gid2",
                table: "leads",
                columns: new[] { "product_key", "locality_gid2" });

            migrationBuilder.CreateIndex(
                name: "ix_search_jobs_locality_gid2",
                table: "search_jobs",
                column: "locality_gid2");

            migrationBuilder.CreateIndex(
                name: "ix_search_jobs_product_key",
                table: "search_jobs",
                column: "product_key");

            migrationBuilder.CreateIndex(
                name: "ix_search_jobs_seller_id_scheduled_at",
                table: "search_jobs",
                columns: new[] { "seller_id", "scheduled_at" });

            migrationBuilder.CreateIndex(
                name: "ix_search_jobs_status_scheduled_at",
                table: "search_jobs",
                columns: new[] { "status", "scheduled_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_leads_localities_locality_gid2",
                table: "leads",
                column: "locality_gid2",
                principalTable: "localities",
                principalColumn: "gid2",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_leads_localities_locality_gid2",
                table: "leads");

            migrationBuilder.DropTable(
                name: "search_jobs");

            migrationBuilder.DropIndex(
                name: "ix_leads_locality_gid2",
                table: "leads");

            migrationBuilder.DropIndex(
                name: "ix_leads_product_key_locality_gid2",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "locality_gid2",
                table: "leads");
        }
    }
}
