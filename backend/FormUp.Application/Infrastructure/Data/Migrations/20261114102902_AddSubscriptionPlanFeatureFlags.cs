using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionPlanFeatureFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "can_use_automations",
                table: "subscription_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "can_use_ingredients",
                table: "subscription_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "can_use_packages",
                table: "subscription_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "can_use_plan_templates",
                table: "subscription_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "can_use_progress_photos",
                table: "subscription_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "can_use_recipes",
                table: "subscription_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "can_use_weight_and_measurements",
                table: "subscription_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Carry the old coarse flags over so existing plans keep what they had: templates, recipes and
            // ingredients followed plan creation, photos and measurements followed messaging, packages followed
            // payments. Automations were open to everyone.
            migrationBuilder.Sql(
                """
                UPDATE subscription_plans
                SET can_use_plan_templates = can_create_plans,
                    can_use_recipes = can_create_plans,
                    can_use_ingredients = can_create_plans,
                    can_use_progress_photos = can_message,
                    can_use_weight_and_measurements = can_message,
                    can_use_packages = can_use_payments,
                    can_use_automations = TRUE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "can_use_automations",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "can_use_ingredients",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "can_use_packages",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "can_use_plan_templates",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "can_use_progress_photos",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "can_use_recipes",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "can_use_weight_and_measurements",
                table: "subscription_plans");
        }
    }
}
