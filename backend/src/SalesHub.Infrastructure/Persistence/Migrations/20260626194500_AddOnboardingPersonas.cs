using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingPersonas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "persona_question",
                table: "onboarding_configs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "persona_key",
                table: "lead_onboardings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "onboarding_personas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    keywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    questions = table.Column<List<string>>(type: "text[]", nullable: false),
                    email_prompt = table.Column<string>(type: "text", nullable: false),
                    provision_url = table.Column<string>(type: "text", nullable: false),
                    provision_name_field = table.Column<string>(type: "text", nullable: false),
                    provision_extra = table.Column<string>(type: "text", nullable: false),
                    success_message = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_onboarding_personas", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transcription_phones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transcription_phones", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transcription_phones_phone",
                table: "transcription_phones",
                column: "phone",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "onboarding_personas");

            migrationBuilder.DropTable(
                name: "transcription_phones");

            migrationBuilder.DropColumn(
                name: "persona_question",
                table: "onboarding_configs");

            migrationBuilder.DropColumn(
                name: "persona_key",
                table: "lead_onboardings");
        }
    }
}
