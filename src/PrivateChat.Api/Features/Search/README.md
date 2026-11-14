# Search（搜索）

对应 docs/DESIGN.md 4.11。`GET /api/search?q=&type=All|Messages|Files|Moments&cursor=&limit=`。

- `SearchService`：三段 SQL（消息正文 + 附件文件名、网盘节点名、动态 + 评论）和相册名，可见性与 Chat / Drive / Moments 模块一致；
  空白分词、每个词都要出现、`% _ \` 按字面；新的在前，(时间, Id) 游标翻页；服务端截摘要。
- 索引：迁移 `Search` 里用 SQL 建的 pg_trgm GIN 部分索引 / 表达式索引（EF 模型不描述），条件与查询一一对应。
- 前端：`web/src/features/search`（搜索页、高亮、离线时搜本机缓存的聊天），聊天跳到某条消息在 `ChatPage` / `MessageList` / `stores/messages.ts ensureSeq`。
- 测试：`tests/.../Integration/SearchTests.cs`、`web/src/features/search/text.spec.ts`、`web/e2e/search.cjs`。
