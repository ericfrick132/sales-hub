using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultiFormatChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PostingChannel_Product_Platform",
                table: "posting_channels");

            migrationBuilder.CreateIndex(
                name: "IX_PostingChannel_Product_Platform_Format",
                table: "posting_channels",
                columns: new[] { "product_key", "platform", "format" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PostingChannel_Product_Platform_Format",
                table: "posting_channels");

            migrationBuilder.CreateIndex(
                name: "IX_PostingChannel_Product_Platform",
                table: "posting_channels",
                columns: new[] { "product_key", "platform" },
                unique: true);
        }
    }
}
