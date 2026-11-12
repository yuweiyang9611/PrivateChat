using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrivateChat.Api.Migrations
{
    /// <inheritdoc />
    public partial class Remote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "accepted_at",
                table: "remote_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "remote_sessions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "remote_sessions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "agent_version",
                table: "devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "os",
                table: "devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pin_failures",
                table: "devices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pin_locked_until",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            // M5 之前没有真正的远程会话；万一有旧行，标成已结束（新列默认值不是合法状态）
            migrationBuilder.Sql("UPDATE remote_sessions SET state = 'Ended', expires_at = started_at, ended_at = COALESCE(ended_at, started_at) WHERE state = '';");

            migrationBuilder.CreateIndex(
                name: "ix_remote_sessions_open_per_device",
                table: "remote_sessions",
                column: "target_device_id",
                unique: true,
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_remote_sessions_started_at",
                table: "remote_sessions",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_remote_sessions_state",
                table: "remote_sessions",
                column: "state",
                filter: "ended_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_remote_sessions_open_per_device",
                table: "remote_sessions");

            migrationBuilder.DropIndex(
                name: "ix_remote_sessions_started_at",
                table: "remote_sessions");

            migrationBuilder.DropIndex(
                name: "ix_remote_sessions_state",
                table: "remote_sessions");

            migrationBuilder.DropColumn(
                name: "accepted_at",
                table: "remote_sessions");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "remote_sessions");

            migrationBuilder.DropColumn(
                name: "state",
                table: "remote_sessions");

            migrationBuilder.DropColumn(
                name: "agent_version",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "os",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "pin_failures",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "pin_locked_until",
                table: "devices");
        }
    }
}
