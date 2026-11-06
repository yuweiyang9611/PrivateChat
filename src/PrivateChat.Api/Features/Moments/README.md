# Moments（家庭动态）— 待实现

对应 docs/DESIGN.md 4.7。计划包含：

- `PostsController`：`GET /api/posts?before=`、`POST /api/posts`（文本 + ≤9 媒体，引用 blobs）、`DELETE`。
- `CommentsController`：`POST /api/posts/{id}/comments`（支持回复）。
- `ReactionsController`：`POST /api/posts/{id}/reactions`。
- `OnThisDayController`：`GET /api/posts/on-this-day`（每日凌晨由 jobs 预计算）。
- `AlbumsController`：手动相册 + 自动按月相册。
- 通知：评论 / 点赞触发 Notifications + Web Push。
