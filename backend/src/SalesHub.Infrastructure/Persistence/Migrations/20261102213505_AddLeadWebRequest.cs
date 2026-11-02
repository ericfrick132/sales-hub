using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadWebRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "web_request_at",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "web_request_json",
                table: "leads",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_leads_web_request_at",
                table: "leads",
                column: "web_request_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_leads_web_request_at",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "web_request_at",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "web_request_json",
                table: "leads");
        }
    }
}
