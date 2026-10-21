using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinderBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "finder_batch_id",
                table: "audience_leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "finder_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    dataset = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    size = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retried_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_finder_batches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audience_leads_finder_batch_id",
                table: "audience_leads",
                column: "finder_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_finder_batches_status",
                table: "finder_batches",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "finder_batches");

            migrationBuilder.DropIndex(
                name: "ix_audience_leads_finder_batch_id",
                table: "audience_leads");

            migrationBuilder.DropColumn(
                name: "finder_batch_id",
                table: "audience_leads");
        }
    }
}
