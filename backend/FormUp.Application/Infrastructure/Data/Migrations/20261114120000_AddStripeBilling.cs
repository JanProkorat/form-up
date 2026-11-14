using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_external_event_at",
                table: "coach_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "processed_webhook_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    event_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_webhook_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_coach_subscriptions_external_customer_id",
                table: "coach_subscriptions",
                column: "external_customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_coach_subscriptions_external_subscription_id",
                table: "coach_subscriptions",
                column: "external_subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_processed_webhook_events_event_id",
                table: "processed_webhook_events",
                column: "event_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_webhook_events");

            migrationBuilder.DropIndex(
                name: "ix_coach_subscriptions_external_customer_id",
                table: "coach_subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_coach_subscriptions_external_subscription_id",
                table: "coach_subscriptions");

            migrationBuilder.DropColumn(
                name: "last_external_event_at",
                table: "coach_subscriptions");
        }
    }
}
