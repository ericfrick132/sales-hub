using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "localities",
                columns: table => new
                {
                    gid2 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    admin_level1gid = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    admin_level1name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    country_code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    country_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    centroid_lat = table.Column<double>(type: "double precision", nullable: false),
                    centroid_lng = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_localities", x => x.gid2);
                });

            migrationBuilder.CreateTable(
                name: "seller_localities",
                columns: table => new
                {
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    locality_gid2 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seller_localities", x => new { x.seller_id, x.locality_gid2 });
                    table.ForeignKey(
                        name: "fk_seller_localities_localities_locality_gid2",
                        column: x => x.locality_gid2,
                        principalTable: "localities",
                        principalColumn: "gid2",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_seller_localities_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_localities_country_code",
                table: "localities",
                column: "country_code");

            migrationBuilder.CreateIndex(
                name: "ix_localities_country_code_admin_level1gid",
                table: "localities",
                columns: new[] { "country_code", "admin_level1gid" });

            migrationBuilder.CreateIndex(
                name: "ix_seller_localities_locality_gid2",
                table: "seller_localities",
                column: "locality_gid2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "seller_localities");

            migrationBuilder.DropTable(
                name: "localities");
        }
    }
}
