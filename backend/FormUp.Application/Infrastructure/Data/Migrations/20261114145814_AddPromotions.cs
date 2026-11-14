using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "current_promotion_ends_at",
                table: "coach_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "current_promotion_id",
                table: "coach_subscriptions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_paid_at",
                table: "coach_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "promotions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    mode = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    code_normalized = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    duration = table.Column<int>(type: "integer", nullable: false),
                    duration_months = table.Column<int>(type: "integer", nullable: true),
                    claim_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claim_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    plan_codes = table.Column<List<string>>(type: "text[]", nullable: false),
                    covers_second_role_add_on = table.Column<bool>(type: "boolean", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    new_coaches_only = table.Column<bool>(type: "boolean", nullable: false),
                    one_use_per_coach = table.Column<bool>(type: "boolean", nullable: false),
                    stripe_coupon_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    stripe_promotion_code_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "promotion_redemptions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    promotion_id = table.Column<long>(type: "bigint", nullable: false),
                    professional_profile_id = table.Column<long>(type: "bigint", nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotion_redemptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_promotion_redemptions_professional_profiles_professional_pr",
                        column: x => x.professional_profile_id,
                        principalTable: "professional_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_promotion_redemptions_promotions_promotion_id",
                        column: x => x.promotion_id,
                        principalTable: "promotions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_coach_subscriptions_current_promotion_id",
                table: "coach_subscriptions",
                column: "current_promotion_id");

            migrationBuilder.CreateIndex(
                name: "ix_promotion_redemptions_professional_profile_id",
                table: "promotion_redemptions",
                column: "professional_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_promotion_redemptions_promotion_id_professional_profile_id",
                table: "promotion_redemptions",
                columns: new[] { "promotion_id", "professional_profile_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_promotions_code_normalized",
                table: "promotions",
                column: "code_normalized");

            migrationBuilder.CreateIndex(
                name: "ix_promotions_public_id",
                table: "promotions",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_promotions_stripe_coupon_id",
                table: "promotions",
                column: "stripe_coupon_id");

            migrationBuilder.CreateIndex(
                name: "ix_promotions_stripe_promotion_code_id",
                table: "promotions",
                column: "stripe_promotion_code_id");

            migrationBuilder.AddForeignKey(
                name: "fk_coach_subscriptions_promotions_current_promotion_id",
                table: "coach_subscriptions",
                column: "current_promotion_id",
                principalTable: "promotions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_coach_subscriptions_promotions_current_promotion_id",
                table: "coach_subscriptions");

            migrationBuilder.DropTable(
                name: "promotion_redemptions");

            migrationBuilder.DropTable(
                name: "promotions");

            migrationBuilder.DropIndex(
                name: "ix_coach_subscriptions_current_promotion_id",
                table: "coach_subscriptions");

            migrationBuilder.DropColumn(
                name: "current_promotion_ends_at",
                table: "coach_subscriptions");

            migrationBuilder.DropColumn(
                name: "current_promotion_id",
                table: "coach_subscriptions");

            migrationBuilder.DropColumn(
                name: "first_paid_at",
                table: "coach_subscriptions");
        }
    }
}
