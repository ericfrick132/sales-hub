using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "demo_event_type_uri",
                table: "onboarding_configs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "chosen_slot",
                table: "lead_onboardings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "demo_booked_at",
                table: "lead_onboardings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "demo_event_uri",
                table: "lead_onboardings",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<List<DateTimeOffset>>(
                name: "offered_slots",
                table: "lead_onboardings",
                type: "timestamp with time zone[]",
                nullable: false,
                defaultValueSql: "'{}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "demo_event_type_uri",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "chosen_slot",
                table: "lead_onboardings");

            migrationBuilder.DropColumn(
                name: "demo_booked_at",
                table: "lead_onboardings");

            migrationBuilder.DropColumn(
                name: "demo_event_uri",
                table: "lead_onboardings");

            migrationBuilder.DropColumn(
                name: "offered_slots",
                table: "lead_onboardings");
        }
    }
}
