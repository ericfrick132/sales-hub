using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArcaNomencladorYRubroLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "arca_activity_code",
                table: "leads",
                type: "character varying(6)",
                maxLength: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "arca_activity_rule",
                table: "leads",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "arca_rules_version",
                table: "leads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "arca_activities",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    group_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    group_description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    division_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    division_description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    section = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    section_description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_arca_activities", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "ix_leads_arca_activity_code",
                table: "leads",
                column: "arca_activity_code",
                filter: "arca_activity_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_arca_activities_group_code",
                table: "arca_activities",
                column: "group_code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "arca_activities");

            migrationBuilder.DropIndex(
                name: "ix_leads_arca_activity_code",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "arca_activity_code",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "arca_activity_rule",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "arca_rules_version",
                table: "leads");
        }
    }
}
