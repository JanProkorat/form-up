using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePromotionAddOnCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "covers_second_role_add_on",
                table: "promotions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "covers_second_role_add_on",
                table: "promotions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
