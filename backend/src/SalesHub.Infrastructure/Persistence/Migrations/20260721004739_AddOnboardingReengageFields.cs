using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingReengageFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reengage_intro",
                table: "onboarding_configs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<List<Guid>>(
                name: "reengage_media_asset_ids",
                table: "onboarding_configs",
                type: "uuid[]",
                nullable: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "reengage_questions",
                table: "onboarding_configs",
                type: "text[]",
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reengage_intro",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "reengage_media_asset_ids",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "reengage_questions",
                table: "onboarding_configs");
        }
    }
}
