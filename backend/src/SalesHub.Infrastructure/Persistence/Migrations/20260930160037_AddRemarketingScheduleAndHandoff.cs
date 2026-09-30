using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRemarketingScheduleAndHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "send_hour_end",
                table: "remarketing_settings");

            migrationBuilder.DropColumn(
                name: "send_hour_start",
                table: "remarketing_settings");

            migrationBuilder.AddColumn<Guid>(
                name: "handoff_seller_id",
                table: "remarketing_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<List<int>>(
                name: "send_weekdays",
                table: "remarketing_settings",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{1,2,3,4}'");

            migrationBuilder.AddColumn<List<string>>(
                name: "send_windows",
                table: "remarketing_settings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{9-12}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "handoff_seller_id",
                table: "remarketing_settings");

            migrationBuilder.DropColumn(
                name: "send_weekdays",
                table: "remarketing_settings");

            migrationBuilder.DropColumn(
                name: "send_windows",
                table: "remarketing_settings");

            migrationBuilder.AddColumn<int>(
                name: "send_hour_end",
                table: "remarketing_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "send_hour_start",
                table: "remarketing_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
