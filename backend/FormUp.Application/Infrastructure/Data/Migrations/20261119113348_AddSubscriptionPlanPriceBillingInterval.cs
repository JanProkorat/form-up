using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionPlanPriceBillingInterval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscription_plan_prices_subscription_plan_id",
                table: "subscription_plan_prices");

            migrationBuilder.AddColumn<int>(
                name: "billing_interval",
                table: "subscription_plan_prices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Every existing price row inherits the cadence of its plan, so no row changes meaning.
            migrationBuilder.Sql(
                """
                UPDATE subscription_plan_prices AS price
                SET billing_interval = plan.billing_interval
                FROM subscription_plans AS plan
                WHERE plan.id = price.subscription_plan_id;
                """);

            migrationBuilder.DropColumn(
                name: "billing_interval",
                table: "subscription_plans");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plan_prices_subscription_plan_id_billing_inter",
                table: "subscription_plan_prices",
                columns: new[] { "subscription_plan_id", "billing_interval" },
                unique: true,
                filter: "valid_to IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscription_plan_prices_subscription_plan_id_billing_inter",
                table: "subscription_plan_prices");

            migrationBuilder.AddColumn<int>(
                name: "billing_interval",
                table: "subscription_plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // The old model allows one open row per plan: close the open annual rows (coaches keep their row).
            migrationBuilder.Sql(
                "UPDATE subscription_plan_prices SET valid_to = now() WHERE valid_to IS NULL AND billing_interval = 1;");

            migrationBuilder.DropColumn(
                name: "billing_interval",
                table: "subscription_plan_prices");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plan_prices_subscription_plan_id",
                table: "subscription_plan_prices",
                column: "subscription_plan_id",
                unique: true,
                filter: "valid_to IS NULL");
        }
    }
}
