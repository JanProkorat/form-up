using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionLocalizedNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name_de",
                table: "promotions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name_en",
                table: "promotions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "name_de",
                table: "promotions");

            migrationBuilder.DropColumn(
                name: "name_en",
                table: "promotions");
        }
    }
}
