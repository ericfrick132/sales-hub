using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "video_campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    instance_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    message_before = table.Column<string>(type: "text", nullable: false),
                    media_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_after = table.Column<string>(type: "text", nullable: false),
                    per_day = table.Column<int>(type: "integer", nullable: false),
                    window_from_hour = table.Column<int>(type: "integer", nullable: false),
                    window_to_hour = table.Column<int>(type: "integer", nullable: false),
                    weekdays = table.Column<List<int>>(type: "integer[]", nullable: false, defaultValueSql: "'{1,2,3,4}'"),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_video_campaigns", x => x.id);
                    table.ForeignKey(
                        name: "fk_video_campaigns_media_assets_media_asset_id",
                        column: x => x.media_asset_id,
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "video_campaign_recipients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_video_campaign_recipients", x => x.id);
                    table.ForeignKey(
                        name: "fk_video_campaign_recipients_leads_lead_id",
                        column: x => x.lead_id,
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_video_campaign_recipients_video_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "video_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_video_campaign_recipients_campaign_id_lead_id",
                table: "video_campaign_recipients",
                columns: new[] { "campaign_id", "lead_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_video_campaign_recipients_lead_id",
                table: "video_campaign_recipients",
                column: "lead_id");

            migrationBuilder.CreateIndex(
                name: "ix_video_campaigns_media_asset_id",
                table: "video_campaigns",
                column: "media_asset_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "video_campaign_recipients");

            migrationBuilder.DropTable(
                name: "video_campaigns");
        }
    }
}
