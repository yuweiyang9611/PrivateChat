# Search（搜索）— 待实现

对应 docs/DESIGN.md 4.11：`GET /api/search?q=&type=`，基于 PostgreSQL `pg_trgm` GIN 索引（消息正文、文件名、动态文本）。
迁移中需 `CREATE EXTENSION IF NOT EXISTS pg_trgm` 并为 messages.body 建 `gin_trgm_ops` 索引（见 AppDbContext 的 TODO）。
