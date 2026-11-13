using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlanModelSecondRoleAddOnAndPriceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The legacy placeholder plans (and any dev subscriptions on them) are replaced by the
            // free/start/pro/studio seed, which is re-created by running the app with `--seed` after
            // migrating (the seed does not run on a normal boot). No production data.
            migrationBuilder.Sql(
                """
                DELETE FROM coach_subscriptions
                WHERE subscription_plan_id IN (
                    SELECT id FROM subscription_plans WHERE code IN ('free', 'small', 'medium', 'big'));
                """);
            migrationBuilder.Sql("DELETE FROM subscription_plans WHERE code IN ('free', 'small', 'medium', 'big');");

            migrationBuilder.CreateTable(
                name: "subscription_plan_prices",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subscription_plan_id = table.Column<long>(type: "bigint", nullable: false),
                    price_minor_units = table.Column<long>(type: "bigint", nullable: false),
                    second_role_add_on_price_minor_units = table.Column<long>(type: "bigint", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    external_price_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    valid_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_plan_prices", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_plan_prices_subscription_plans_subscription_pl",
                        column: x => x.subscription_plan_id,
                        principalTable: "subscription_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Any plan that survives (admin-created in dev) gets one open price row from its
            // current price fields, carrying the external price id over before that column goes.
            migrationBuilder.Sql(
                """
                INSERT INTO subscription_plan_prices
                    (subscription_plan_id, price_minor_units, currency, external_price_id, valid_from, date_created)
                SELECT id, price_minor_units, currency, external_price_id, now(), now()
                FROM subscription_plans;
                """);

            migrationBuilder.DropColumn(
                name: "applicable_roles",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "external_price_id",
                table: "subscription_plans");

            // Surviving plans were applicable to roles as configured before; the closest
            // equivalent in the new model is "both roles included".
            migrationBuilder.AddColumn<bool>(
                name: "both_roles_included",
                table: "subscription_plans",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "second_role_add_on_price_minor_units",
                table: "subscription_plans",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "pays_second_role_add_on",
                table: "coach_subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "pending_subscription_plan_price_id",
                table: "coach_subscriptions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "subscription_plan_price_id",
                table: "coach_subscriptions",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE coach_subscriptions cs
                SET subscription_plan_price_id = spp.id
                FROM subscription_plan_prices spp
                WHERE spp.subscription_plan_id = cs.subscription_plan_id AND spp.valid_to IS NULL;
                """);

            migrationBuilder.AlterColumn<long>(
                name: "subscription_plan_price_id",
                table: "coach_subscriptions",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_coach_subscriptions_pending_subscription_plan_price_id",
                table: "coach_subscriptions",
                column: "pending_subscription_plan_price_id");

            migrationBuilder.CreateIndex(
                name: "ix_coach_subscriptions_subscription_plan_price_id",
                table: "coach_subscriptions",
                column: "subscription_plan_price_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plan_prices_subscription_plan_id",
                table: "subscription_plan_prices",
                column: "subscription_plan_id",
                unique: true,
                filter: "valid_to IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_coach_subscriptions_subscription_plan_prices_pending_subscr",
                table: "coach_subscriptions",
                column: "pending_subscription_plan_price_id",
                principalTable: "subscription_plan_prices",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_coach_subscriptions_subscription_plan_prices_subscription_p",
                table: "coach_subscriptions",
                column: "subscription_plan_price_id",
                principalTable: "subscription_plan_prices",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_coach_subscriptions_subscription_plan_prices_pending_subscr",
                table: "coach_subscriptions");

            migrationBuilder.DropForeignKey(
                name: "fk_coach_subscriptions_subscription_plan_prices_subscription_p",
                table: "coach_subscriptions");

            migrationBuilder.DropTable(
                name: "subscription_plan_prices");

            migrationBuilder.DropIndex(
                name: "ix_coach_subscriptions_pending_subscription_plan_price_id",
                table: "coach_subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_coach_subscriptions_subscription_plan_price_id",
                table: "coach_subscriptions");

            migrationBuilder.DropColumn(
                name: "both_roles_included",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "second_role_add_on_price_minor_units",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "pays_second_role_add_on",
                table: "coach_subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_subscription_plan_price_id",
                table: "coach_subscriptions");

            migrationBuilder.DropColumn(
                name: "subscription_plan_price_id",
                table: "coach_subscriptions");

            migrationBuilder.AddColumn<int>(
                name: "applicable_roles",
                table: "subscription_plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "external_price_id",
                table: "subscription_plans",
                type: "text",
                nullable: true);
        }
    }
}
