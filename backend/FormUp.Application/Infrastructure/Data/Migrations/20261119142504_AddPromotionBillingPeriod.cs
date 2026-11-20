using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionBillingPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "billing_period",
                table: "promotions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "stripe_annual_coupon_id",
                table: "promotions",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_promotions_stripe_annual_coupon_id",
                table: "promotions",
                column: "stripe_annual_coupon_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_promotions_stripe_annual_coupon_id",
                table: "promotions");

            migrationBuilder.DropColumn(
                name: "billing_period",
                table: "promotions");

            migrationBuilder.DropColumn(
                name: "stripe_annual_coupon_id",
                table: "promotions");
        }
    }
}
