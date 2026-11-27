using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormUp.Application.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessageClientMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "client_message_id",
                table: "chat_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_client_message_id",
                table: "chat_messages",
                columns: new[] { "conversation_id", "sender_user_id", "client_message_id" },
                unique: true,
                filter: "client_message_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_chat_messages_client_message_id",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "client_message_id",
                table: "chat_messages");
        }
    }
}
