using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrivateChat.Api.Migrations
{
    /// <inheritdoc />
    public partial class Calls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mid",
                table: "call_tracks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "answered_at",
                table: "call_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "end_reason",
                table: "call_sessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "cam_on",
                table: "call_participants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "cf_pull_session_id",
                table: "call_participants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_seen_at",
                table: "call_participants",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<bool>(
                name: "mic_on",
                table: "call_participants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_call_sessions_active_conversation",
                table: "call_sessions",
                column: "conversation_id",
                unique: true,
                filter: "ended_at IS NULL AND conversation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_call_sessions_started_at",
                table: "call_sessions",
                column: "started_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_call_sessions_active_conversation",
                table: "call_sessions");

            migrationBuilder.DropIndex(
                name: "ix_call_sessions_started_at",
                table: "call_sessions");

            migrationBuilder.DropColumn(
                name: "mid",
                table: "call_tracks");

            migrationBuilder.DropColumn(
                name: "answered_at",
                table: "call_sessions");

            migrationBuilder.DropColumn(
                name: "end_reason",
                table: "call_sessions");

            migrationBuilder.DropColumn(
                name: "cam_on",
                table: "call_participants");

            migrationBuilder.DropColumn(
                name: "cf_pull_session_id",
                table: "call_participants");

            migrationBuilder.DropColumn(
                name: "last_seen_at",
                table: "call_participants");

            migrationBuilder.DropColumn(
                name: "mic_on",
                table: "call_participants");
        }
    }
}
