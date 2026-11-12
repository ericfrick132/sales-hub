using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingQuickNudge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "quick_nudge",
                table: "onboarding_configs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "quick_nudge_after_demo",
                table: "onboarding_configs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "quick_nudge_at",
                table: "lead_onboardings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "quick_nudge",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "quick_nudge_after_demo",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "quick_nudge_at",
                table: "lead_onboardings");
        }
    }
}
