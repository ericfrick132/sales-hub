using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "onboarding_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    intro = table.Column<string>(type: "text", nullable: false),
                    questions = table.Column<List<string>>(type: "text[]", nullable: false),
                    email_prompt = table.Column<string>(type: "text", nullable: false),
                    provision_url = table.Column<string>(type: "text", nullable: false),
                    provision_name_field = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    success_message = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_onboarding_configs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_onboarding_configs_product_key",
                table: "onboarding_configs",
                column: "product_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "onboarding_configs");
        }
    }
}
