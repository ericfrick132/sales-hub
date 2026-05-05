using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "media_asset_id",
                table: "message_outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "media_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_assets_products_product_key",
                        column: x => x.product_key,
                        principalTable: "products",
                        principalColumn: "product_key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_message_outbox_media_asset_id",
                table: "message_outbox",
                column: "media_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_product_key",
                table: "media_assets",
                column: "product_key");

            migrationBuilder.AddForeignKey(
                name: "fk_message_outbox_media_assets_media_asset_id",
                table: "message_outbox",
                column: "media_asset_id",
                principalTable: "media_assets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_message_outbox_media_assets_media_asset_id",
                table: "message_outbox");

            migrationBuilder.DropTable(
                name: "media_assets");

            migrationBuilder.DropIndex(
                name: "ix_message_outbox_media_asset_id",
                table: "message_outbox");

            migrationBuilder.DropColumn(
                name: "media_asset_id",
                table: "message_outbox");
        }
    }
}
