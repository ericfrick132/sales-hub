using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPostingChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "posting_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    buffer_channel_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    asset_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    prompt_template = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posting_channels", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostingChannel_Product_Platform",
                table: "posting_channels",
                columns: new[] { "product_key", "platform" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "posting_channels");
        }
    }
}
