using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContentTypesAndLanding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "post_type",
                table: "social_posts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "landing_knowledge",
                table: "posting_profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "landing_knowledge_at",
                table: "posting_profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "landing_url",
                table: "posting_profiles",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "post_type",
                table: "social_posts");

            migrationBuilder.DropColumn(
                name: "landing_knowledge",
                table: "posting_profiles");

            migrationBuilder.DropColumn(
                name: "landing_knowledge_at",
                table: "posting_profiles");

            migrationBuilder.DropColumn(
                name: "landing_url",
                table: "posting_profiles");
        }
    }
}
