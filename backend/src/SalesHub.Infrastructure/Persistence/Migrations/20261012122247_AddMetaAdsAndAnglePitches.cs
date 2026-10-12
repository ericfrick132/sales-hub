using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaAdsAndAnglePitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "angle_pitches",
                table: "onboarding_configs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "known_business_question",
                table: "onboarding_configs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "meta_ads",
                columns: table => new
                {
                    ad_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    account_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    rubro = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    angle = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    business_ref = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    prefill_text = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    body = table.Column<string>(type: "text", nullable: true),
                    destination_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ad_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meta_ads", x => x.ad_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_meta_ads_product_key",
                table: "meta_ads",
                column: "product_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meta_ads");

            migrationBuilder.DropColumn(
                name: "angle_pitches",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "known_business_question",
                table: "onboarding_configs");
        }
    }
}
