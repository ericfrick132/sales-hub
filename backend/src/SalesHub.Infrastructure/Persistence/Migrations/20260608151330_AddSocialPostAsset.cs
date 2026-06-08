using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialPostAsset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "social_post_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    social_post_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mime_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_post_assets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_social_post_assets_social_post_id",
                table: "social_post_assets",
                column: "social_post_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "social_post_assets");
        }
    }
}
