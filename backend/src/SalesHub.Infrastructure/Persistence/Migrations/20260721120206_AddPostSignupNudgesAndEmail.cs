using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPostSignupNudgesAndEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "post_signup_checkin",
                table: "onboarding_configs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "trial_discount_nudge",
                table: "onboarding_configs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "checkin_sent_at",
                table: "lead_onboardings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discount_nudge_sent_at",
                table: "lead_onboardings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "post_signup_checkin",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "trial_discount_nudge",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "checkin_sent_at",
                table: "lead_onboardings");

            migrationBuilder.DropColumn(
                name: "discount_nudge_sent_at",
                table: "lead_onboardings");
        }
    }
}
