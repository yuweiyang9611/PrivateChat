using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrivateChat.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoteTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_telemetry",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    agent_version = table.Column<string>(type: "text", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_telemetry", x => x.device_id);
                    table.ForeignKey(
                        name: "fk_device_telemetry_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "remote_session_telemetry",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    agent = table.Column<string>(type: "jsonb", nullable: true),
                    viewer = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_session_telemetry", x => x.session_id);
                    table.ForeignKey(
                        name: "fk_remote_session_telemetry_remote_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "remote_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_remote_session_telemetry_device_id",
                table: "remote_session_telemetry",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_remote_session_telemetry_updated_at",
                table: "remote_session_telemetry",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_telemetry");

            migrationBuilder.DropTable(
                name: "remote_session_telemetry");
        }
    }
}
