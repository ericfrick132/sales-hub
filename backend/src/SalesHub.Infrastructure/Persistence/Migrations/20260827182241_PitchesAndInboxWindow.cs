using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PitchesAndInboxWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ad_id",
                table: "leads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ad_source_url",
                table: "leads",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ad_title",
                table: "leads",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "conversation_closed_at",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ctwa_clid",
                table: "leads",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_inbound_at",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "tags",
                table: "leads",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.CreateTable(
                name: "conversation_feedbacks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    rated_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversation_feedbacks", x => x.id);
                    table.ForeignKey(
                        name: "fk_conversation_feedbacks_leads_lead_id",
                        column: x => x.lead_id,
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_conversation_feedbacks_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "pitches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    ad_ids = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    trigger_text = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    auto_tag_on_reply = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status_on_reply = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ai_after_pitch = table.Column<bool>(type: "boolean", nullable: false),
                    reply_delay_min_sec = table.Column<int>(type: "integer", nullable: false),
                    reply_delay_max_sec = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pitches", x => x.id);
                    table.ForeignKey(
                        name: "fk_pitches_products_product_key",
                        column: x => x.product_key,
                        principalTable: "products",
                        principalColumn: "product_key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lead_pitch_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pitch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_index = table.Column<int>(type: "integer", nullable: false),
                    step_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_step_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    followups_sent = table.Column<int>(type: "integer", nullable: false),
                    last_followup_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_reply_after_pitch_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replies = table.Column<int>(type: "integer", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    gave_up_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_pitch_states", x => x.id);
                    table.ForeignKey(
                        name: "fk_lead_pitch_states_leads_lead_id",
                        column: x => x.lead_id,
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_lead_pitch_states_pitches_pitch_id",
                        column: x => x.pitch_id,
                        principalTable: "pitches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_leads_ad_id",
                table: "leads",
                column: "ad_id",
                filter: "ad_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_leads_last_inbound_at",
                table: "leads",
                column: "last_inbound_at");

            migrationBuilder.CreateIndex(
                name: "ix_conversation_feedbacks_lead_id",
                table: "conversation_feedbacks",
                column: "lead_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversation_feedbacks_product_key_created_at",
                table: "conversation_feedbacks",
                columns: new[] { "product_key", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_conversation_feedbacks_seller_id",
                table: "conversation_feedbacks",
                column: "seller_id");

            migrationBuilder.CreateIndex(
                name: "ix_lead_pitch_states_lead_id",
                table: "lead_pitch_states",
                column: "lead_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lead_pitch_states_next_step_due_at",
                table: "lead_pitch_states",
                column: "next_step_due_at");

            migrationBuilder.CreateIndex(
                name: "ix_lead_pitch_states_pitch_id_completed_at",
                table: "lead_pitch_states",
                columns: new[] { "pitch_id", "completed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pitches_product_key_active",
                table: "pitches",
                columns: new[] { "product_key", "active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_feedbacks");

            migrationBuilder.DropTable(
                name: "lead_pitch_states");

            migrationBuilder.DropTable(
                name: "pitches");

            migrationBuilder.DropIndex(
                name: "ix_leads_ad_id",
                table: "leads");

            migrationBuilder.DropIndex(
                name: "ix_leads_last_inbound_at",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "ad_id",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "ad_source_url",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "ad_title",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "conversation_closed_at",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "ctwa_clid",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "last_inbound_at",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "tags",
                table: "leads");
        }
    }
}
