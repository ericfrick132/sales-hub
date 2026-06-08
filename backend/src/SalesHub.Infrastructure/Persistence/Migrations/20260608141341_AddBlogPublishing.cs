using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBlogPublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "posts_per_run",
                table: "seo_sites",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "publish_cadence_hours",
                table: "seo_sites",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "repo_branch",
                table: "seo_sites",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "repo_full_name",
                table: "seo_sites",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "repo_public_dir",
                table: "seo_sites",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "published_url",
                table: "seo_articles",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "posts_per_run",
                table: "seo_sites");

            migrationBuilder.DropColumn(
                name: "publish_cadence_hours",
                table: "seo_sites");

            migrationBuilder.DropColumn(
                name: "repo_branch",
                table: "seo_sites");

            migrationBuilder.DropColumn(
                name: "repo_full_name",
                table: "seo_sites");

            migrationBuilder.DropColumn(
                name: "repo_public_dir",
                table: "seo_sites");

            migrationBuilder.DropColumn(
                name: "published_url",
                table: "seo_articles");
        }
    }
}
