using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGreetingAndPricesStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "known_business_greeting",
                table: "onboarding_configs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<List<Guid>>(
                name: "prices_media_asset_ids",
                table: "onboarding_configs",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "prices_message",
                table: "onboarding_configs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "start_question",
                table: "onboarding_configs",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "known_business_greeting",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "prices_media_asset_ids",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "prices_message",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "start_question",
                table: "onboarding_configs");
        }
    }
}
