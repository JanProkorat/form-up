using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationRunRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempt_count",
                table: "automation_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_retry_at",
                table: "automation_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_runs_next_retry_at",
                table: "automation_runs",
                column: "next_retry_at",
                filter: "next_retry_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_automation_runs_next_retry_at",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "next_retry_at",
                table: "automation_runs");
        }
    }
}
