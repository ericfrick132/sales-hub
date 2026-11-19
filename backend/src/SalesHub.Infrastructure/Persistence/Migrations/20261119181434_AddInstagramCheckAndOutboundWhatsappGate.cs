using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstagramCheckAndOutboundWhatsappGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "instagram_bio_link",
                table: "leads",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "instagram_checked_at",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "instagram_followers",
                table: "leads",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "outbound_require_whatsapp",
                table: "evolution_instances",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "instagram_bio_link",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "instagram_checked_at",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "instagram_followers",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "outbound_require_whatsapp",
                table: "evolution_instances");
        }
    }
}
