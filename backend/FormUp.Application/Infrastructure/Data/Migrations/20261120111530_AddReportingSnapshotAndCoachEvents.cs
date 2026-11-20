using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReportingSnapshotAndCoachEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "coach_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    coach_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    plan_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    previous_plan_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    full_monthly_revenue_effect_minor_units = table.Column<long>(type: "bigint", nullable: false),
                    discounted_monthly_revenue_effect_minor_units = table.Column<long>(type: "bigint", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coach_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_coach_events_users_coach_user_id",
                        column: x => x.coach_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "daily_report_snapshots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    total_coach_count = table.Column<int>(type: "integer", nullable: false),
                    paying_coach_count = table.Column<int>(type: "integer", nullable: false),
                    free_coach_count = table.Column<int>(type: "integer", nullable: false),
                    given_free_coach_count = table.Column<int>(type: "integer", nullable: false),
                    active_coaches_last30days = table.Column<int>(type: "integer", nullable: false),
                    active_clients_last7days = table.Column<int>(type: "integer", nullable: false),
                    plan_weeks_published_last7days = table.Column<int>(type: "integer", nullable: false),
                    free_coaches_at_client_cap = table.Column<int>(type: "integer", nullable: false),
                    median_clients_per_coach = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    monthly_revenue_after_promotions = table.Column<string>(type: "jsonb", nullable: true),
                    monthly_revenue_full = table.Column<string>(type: "jsonb", nullable: true),
                    paying_coaches_by_plan = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_report_snapshots", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_coach_events_coach_user_id",
                table: "coach_events",
                column: "coach_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_coach_events_event_type_occurred_at",
                table: "coach_events",
                columns: new[] { "event_type", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_coach_events_idempotency_key",
                table: "coach_events",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_daily_report_snapshots_day",
                table: "daily_report_snapshots",
                column: "day",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coach_events");

            migrationBuilder.DropTable(
                name: "daily_report_snapshots");
        }
    }
}
