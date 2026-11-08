using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrivateChat.Api.Migrations
{
    /// <inheritdoc />
    public partial class Drive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_drive_nodes_parent_id_name",
                table: "drive_nodes");

            migrationBuilder.AddColumn<bool>(
                name: "child_read_only",
                table: "spaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "deleted_by",
                table: "drive_nodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "trash_root_id",
                table: "drive_nodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "duration_seconds",
                table: "blobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "height",
                table: "blobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "media_probed_at",
                table: "blobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "taken_at",
                table: "blobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "width",
                table: "blobs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "blob_claims",
                columns: table => new
                {
                    blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blob_claims", x => new { x.blob_id, x.user_id });
                });

            // 存量数据：上传者对自己上传的内容天然有凭证
            migrationBuilder.Sql("INSERT INTO blob_claims (blob_id, user_id, created_at) SELECT id, created_by, created_at FROM blobs ON CONFLICT DO NOTHING;");

            migrationBuilder.CreateIndex(
                name: "ix_spaces_type_owner_user_id",
                table: "spaces",
                columns: new[] { "type", "owner_user_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_message_attachments_blob_id",
                table: "message_attachments",
                column: "blob_id");

            migrationBuilder.CreateIndex(
                name: "ix_drive_nodes_blob_id",
                table: "drive_nodes",
                column: "blob_id");

            migrationBuilder.CreateIndex(
                name: "ix_drive_nodes_space_id_parent_id_name",
                table: "drive_nodes",
                columns: new[] { "space_id", "parent_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_drive_nodes_trash_root_id",
                table: "drive_nodes",
                column: "trash_root_id");

            migrationBuilder.CreateIndex(
                name: "ix_blobs_media_probed_at",
                table: "blobs",
                column: "media_probed_at");

            migrationBuilder.CreateIndex(
                name: "ix_blob_claims_user_id",
                table: "blob_claims",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blob_claims");

            migrationBuilder.DropIndex(
                name: "ix_spaces_type_owner_user_id",
                table: "spaces");

            migrationBuilder.DropIndex(
                name: "ix_message_attachments_blob_id",
                table: "message_attachments");

            migrationBuilder.DropIndex(
                name: "ix_drive_nodes_blob_id",
                table: "drive_nodes");

            migrationBuilder.DropIndex(
                name: "ix_drive_nodes_space_id_parent_id_name",
                table: "drive_nodes");

            migrationBuilder.DropIndex(
                name: "ix_drive_nodes_trash_root_id",
                table: "drive_nodes");

            migrationBuilder.DropIndex(
                name: "ix_blobs_media_probed_at",
                table: "blobs");

            migrationBuilder.DropColumn(
                name: "child_read_only",
                table: "spaces");

            migrationBuilder.DropColumn(
                name: "deleted_by",
                table: "drive_nodes");

            migrationBuilder.DropColumn(
                name: "trash_root_id",
                table: "drive_nodes");

            migrationBuilder.DropColumn(
                name: "duration_seconds",
                table: "blobs");

            migrationBuilder.DropColumn(
                name: "height",
                table: "blobs");

            migrationBuilder.DropColumn(
                name: "media_probed_at",
                table: "blobs");

            migrationBuilder.DropColumn(
                name: "taken_at",
                table: "blobs");

            migrationBuilder.DropColumn(
                name: "width",
                table: "blobs");

            migrationBuilder.CreateIndex(
                name: "ix_drive_nodes_parent_id_name",
                table: "drive_nodes",
                columns: new[] { "parent_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL");
        }
    }
}
