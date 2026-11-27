using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationClientOverrideCheckInForm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "check_in_questionnaire_id",
                table: "automation_client_overrides",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_client_overrides_check_in_questionnaire_id",
                table: "automation_client_overrides",
                column: "check_in_questionnaire_id");

            migrationBuilder.AddForeignKey(
                name: "fk_automation_client_overrides_questionnaires_check_in_questio",
                table: "automation_client_overrides",
                column: "check_in_questionnaire_id",
                principalTable: "questionnaires",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_automation_client_overrides_questionnaires_check_in_questio",
                table: "automation_client_overrides");

            migrationBuilder.DropIndex(
                name: "ix_automation_client_overrides_check_in_questionnaire_id",
                table: "automation_client_overrides");

            migrationBuilder.DropColumn(
                name: "check_in_questionnaire_id",
                table: "automation_client_overrides");
        }
    }
}
