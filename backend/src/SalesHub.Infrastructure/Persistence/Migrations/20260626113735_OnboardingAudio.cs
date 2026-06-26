using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingAudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "use_pitch_audio",
                table: "onboarding_configs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "onboarding_audios",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    mime_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_onboarding_audios", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_onboarding_audios_product_key",
                table: "onboarding_audios",
                column: "product_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "onboarding_audios");

            migrationBuilder.DropColumn(
                name: "use_pitch_audio",
                table: "onboarding_configs");
        }
    }
}
