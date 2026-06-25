using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSellerGoals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "demo_scheduled_at",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "seller_goals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    metric = table.Column<int>(type: "integer", nullable: false),
                    period = table.Column<int>(type: "integer", nullable: false),
                    target = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seller_goals", x => x.id);
                    table.ForeignKey(
                        name: "fk_seller_goals_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_seller_goals_seller_id_product_key_metric_period",
                table: "seller_goals",
                columns: new[] { "seller_id", "product_key", "metric", "period" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "seller_goals");

            migrationBuilder.DropColumn(
                name: "demo_scheduled_at",
                table: "leads");
        }
    }
}
