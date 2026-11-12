using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrivateChat.Api.Migrations
{
    /// <inheritdoc />
    public partial class Moments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_comments_post_id",
                table: "comments");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "albums",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // 已有相册的"最后改动时间"从创建时间开始算
            migrationBuilder.Sql("UPDATE albums SET updated_at = created_at;");

            migrationBuilder.CreateIndex(
                name: "ix_posts_author_id",
                table: "posts",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_post_media_blob_id",
                table: "post_media",
                column: "blob_id");

            migrationBuilder.CreateIndex(
                name: "ix_comments_post_id_created_at",
                table: "comments",
                columns: new[] { "post_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_album_items_blob_id",
                table: "album_items",
                column: "blob_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_posts_author_id",
                table: "posts");

            migrationBuilder.DropIndex(
                name: "ix_post_media_blob_id",
                table: "post_media");

            migrationBuilder.DropIndex(
                name: "ix_comments_post_id_created_at",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "ix_album_items_blob_id",
                table: "album_items");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "albums");

            migrationBuilder.CreateIndex(
                name: "ix_comments_post_id",
                table: "comments",
                column: "post_id");
        }
    }
}
