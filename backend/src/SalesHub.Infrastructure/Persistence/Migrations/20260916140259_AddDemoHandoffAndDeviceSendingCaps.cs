using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoHandoffAndDeviceSendingCaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "demo_handoff_seller_id",
                table: "sellers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "origin_seller_id",
                table: "leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "daily_new_chat_cap",
                table: "devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "sends_cold_leads",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "demo_handoff_seller_id",
                table: "sellers");

            migrationBuilder.DropColumn(
                name: "origin_seller_id",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "daily_new_chat_cap",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "sends_cold_leads",
                table: "devices");
        }
    }
}
