using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_onboardings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step = table.Column<int>(type: "integer", nullable: false),
                    contact_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    gym_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    member_count = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    payment_method = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    gym_retries = table.Column<int>(type: "integer", nullable: false),
                    provisioned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    access_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_onboardings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lead_onboardings_lead_id",
                table: "lead_onboardings",
                column: "lead_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_onboardings");
        }
    }
}
