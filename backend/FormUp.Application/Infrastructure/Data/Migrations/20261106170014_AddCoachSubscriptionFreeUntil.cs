using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachSubscriptionFreeUntil : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "free_until",
                table: "coach_subscriptions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "free_until",
                table: "coach_subscriptions");
        }
    }
}
