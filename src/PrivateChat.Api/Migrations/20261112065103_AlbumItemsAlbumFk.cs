using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrivateChat.Api.Migrations
{
    /// <inheritdoc />
    public partial class AlbumItemsAlbumFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 之前没有外键：先清掉指向已删除相册的孤儿条目，否则建不了外键
            migrationBuilder.Sql("DELETE FROM album_items ai WHERE NOT EXISTS (SELECT 1 FROM albums a WHERE a.id = ai.album_id);");

            migrationBuilder.AddForeignKey(
                name: "fk_album_items_albums_album_id",
                table: "album_items",
                column: "album_id",
                principalTable: "albums",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_album_items_albums_album_id",
                table: "album_items");
        }
    }
}
