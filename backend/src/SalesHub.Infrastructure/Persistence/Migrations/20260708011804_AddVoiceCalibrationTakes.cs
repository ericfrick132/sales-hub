using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVoiceCalibrationTakes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "voice_calibration_takes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    script_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    script_text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    whatsapp_message_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_calibration_takes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_voice_calibration_takes_script_key",
                table: "voice_calibration_takes",
                column: "script_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "voice_calibration_takes");
        }
    }
}
