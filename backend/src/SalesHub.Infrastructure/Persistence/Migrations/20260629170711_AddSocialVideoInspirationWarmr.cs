using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialVideoInspirationWarmr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "inspiration_post_id",
                table: "social_posts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target",
                table: "social_posts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "warmr_account",
                table: "social_posts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "distribution",
                table: "posting_channels",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "warmr_account",
                table: "posting_channels",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "curated",
                table: "competitor_posts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "curated_at",
                table: "competitor_posts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_video",
                table: "competitor_posts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "media_url",
                table: "competitor_posts",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "thumbnail_url",
                table: "competitor_posts",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_social_posts_inspiration_post_id",
                table: "social_posts",
                column: "inspiration_post_id");

            migrationBuilder.CreateIndex(
                name: "ix_competitor_posts_curated",
                table: "competitor_posts",
                column: "curated");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_social_posts_inspiration_post_id",
                table: "social_posts");

            migrationBuilder.DropIndex(
                name: "ix_competitor_posts_curated",
                table: "competitor_posts");

            migrationBuilder.DropColumn(
                name: "inspiration_post_id",
                table: "social_posts");

            migrationBuilder.DropColumn(
                name: "target",
                table: "social_posts");

            migrationBuilder.DropColumn(
                name: "warmr_account",
                table: "social_posts");

            migrationBuilder.DropColumn(
                name: "distribution",
                table: "posting_channels");

            migrationBuilder.DropColumn(
                name: "warmr_account",
                table: "posting_channels");

            migrationBuilder.DropColumn(
                name: "curated",
                table: "competitor_posts");

            migrationBuilder.DropColumn(
                name: "curated_at",
                table: "competitor_posts");

            migrationBuilder.DropColumn(
                name: "is_video",
                table: "competitor_posts");

            migrationBuilder.DropColumn(
                name: "media_url",
                table: "competitor_posts");

            migrationBuilder.DropColumn(
                name: "thumbnail_url",
                table: "competitor_posts");
        }
    }
}
