using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudienceLeads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audience_leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    competitor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    segment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    external_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    category = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    address = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    city = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    province = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    postal_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    country = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    phone_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    whatsapp_probable = table.Column<bool>(type: "boolean", nullable: false),
                    phone_source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    instagram = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    website = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    place_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    places_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    raw_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audience_leads", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audience_leads_dataset_external_id",
                table: "audience_leads",
                columns: new[] { "dataset", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audience_leads_instagram",
                table: "audience_leads",
                column: "instagram");

            migrationBuilder.CreateIndex(
                name: "ix_audience_leads_phone",
                table: "audience_leads",
                column: "phone");

            migrationBuilder.CreateIndex(
                name: "ix_audience_leads_product_key_country_segment",
                table: "audience_leads",
                columns: new[] { "product_key", "country", "segment" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audience_leads");
        }
    }
}
