using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "handoff_after_questions",
                table: "onboarding_configs",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<string>(
                name: "handoff_message",
                table: "onboarding_configs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "handoff_seller_id",
                table: "onboarding_configs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "present_as",
                table: "onboarding_configs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "handoff_after_questions",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "handoff_message",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "handoff_seller_id",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "present_as",
                table: "onboarding_configs");
        }
    }
}
