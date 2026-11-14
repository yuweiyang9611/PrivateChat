using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrivateChat.Api.Migrations
{
    /// <summary>
    /// 搜索（docs/DESIGN.md 4.11）：pg_trgm 三元组 GIN 索引，支撑 ILIKE '%词%' 的中文子串匹配。
    /// 部分索引的条件与 SearchService 里的查询条件一一对应，查询才用得上；表达式索引（附件文件名）EF 模型描述不了，所以这里直接写 SQL。
    /// </summary>
    public partial class Search : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("CREATE INDEX ix_messages_body_trgm ON messages USING gin (body gin_trgm_ops) WHERE type = 'Text' AND deleted_at IS NULL;");
            migrationBuilder.Sql("CREATE INDEX ix_message_attachments_name_trgm ON message_attachments USING gin ((meta ->> 'name') gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_drive_nodes_name_trgm ON drive_nodes USING gin (name gin_trgm_ops) WHERE deleted_at IS NULL;");
            migrationBuilder.Sql("CREATE INDEX ix_posts_text_trgm ON posts USING gin (text gin_trgm_ops) WHERE deleted_at IS NULL;");
            migrationBuilder.Sql("CREATE INDEX ix_comments_text_trgm ON comments USING gin (text gin_trgm_ops) WHERE deleted_at IS NULL;");
            migrationBuilder.Sql("CREATE INDEX ix_albums_title_trgm ON albums USING gin (title gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_albums_title_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_comments_text_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_posts_text_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_drive_nodes_name_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_message_attachments_name_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_messages_body_trgm;");
            // 扩展留着：别的东西可能也在用，删掉没有好处
        }
    }
}
