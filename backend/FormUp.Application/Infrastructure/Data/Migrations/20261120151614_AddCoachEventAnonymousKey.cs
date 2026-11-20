using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachEventAnonymousKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "anonymous_coach_key",
                table: "coach_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_coach_events_anonymous_coach_key",
                table: "coach_events",
                column: "anonymous_coach_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_coach_events_anonymous_coach_key",
                table: "coach_events");

            migrationBuilder.DropColumn(
                name: "anonymous_coach_key",
                table: "coach_events");
        }
    }
}
