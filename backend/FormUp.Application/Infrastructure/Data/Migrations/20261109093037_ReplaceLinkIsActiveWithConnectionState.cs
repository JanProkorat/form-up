using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceLinkIsActiveWithConnectionState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "connection_state",
                table: "client_professional_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Carry the old flag over: ended links (is_active = false) become Ended (1), live links stay Connected (0).
            migrationBuilder.Sql(
                "UPDATE client_professional_links SET connection_state = CASE WHEN is_active THEN 0 ELSE 1 END;");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "client_professional_links");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "client_professional_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Ended (1) maps back to inactive; every other state counts as live.
            migrationBuilder.Sql(
                "UPDATE client_professional_links SET is_active = (connection_state <> 1);");

            migrationBuilder.DropColumn(
                name: "connection_state",
                table: "client_professional_links");
        }
    }
}
