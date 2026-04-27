using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGeoFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "latitude",
                table: "leads",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "longitude",
                table: "leads",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "geoname_id",
                table: "cities_queue",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "latitude",
                table: "cities_queue",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "longitude",
                table: "cities_queue",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "population",
                table: "cities_queue",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_leads_latitude_longitude",
                table: "leads",
                columns: new[] { "latitude", "longitude" });

            migrationBuilder.CreateIndex(
                name: "ix_cities_queue_country_population_bucket",
                table: "cities_queue",
                columns: new[] { "country", "population_bucket" });

            migrationBuilder.CreateIndex(
                name: "ix_cities_queue_geoname_id",
                table: "cities_queue",
                column: "geoname_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_leads_latitude_longitude",
                table: "leads");

            migrationBuilder.DropIndex(
                name: "ix_cities_queue_country_population_bucket",
                table: "cities_queue");

            migrationBuilder.DropIndex(
                name: "ix_cities_queue_geoname_id",
                table: "cities_queue");

            migrationBuilder.DropColumn(
                name: "latitude",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "longitude",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "geoname_id",
                table: "cities_queue");

            migrationBuilder.DropColumn(
                name: "latitude",
                table: "cities_queue");

            migrationBuilder.DropColumn(
                name: "longitude",
                table: "cities_queue");

            migrationBuilder.DropColumn(
                name: "population",
                table: "cities_queue");
        }
    }
}
