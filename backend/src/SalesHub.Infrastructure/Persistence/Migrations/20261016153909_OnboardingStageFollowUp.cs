using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingStageFollowUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "follow_up_count",
                table: "lead_onboardings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "follow_up_step",
                table: "lead_onboardings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_follow_up_at",
                table: "lead_onboardings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "follow_up_count",
                table: "lead_onboardings");

            migrationBuilder.DropColumn(
                name: "follow_up_step",
                table: "lead_onboardings");

            migrationBuilder.DropColumn(
                name: "last_follow_up_at",
                table: "lead_onboardings");
        }
    }
}
