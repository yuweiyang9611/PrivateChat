# Moments（家庭动态）

对应 docs/DESIGN.md 4.7。

- `MomentsController`（`api/posts`）：动态时间线（游标分页）、发布（文本 + ≤9 张照片 / 视频，可见范围"家人" / "仅自己"）、删除、评论与回复、动态 / 评论点赞、那年今天。
- `AlbumsController`（`api/albums`）：全家共享的手动相册（增删改、加 / 移出照片）与按月自动相册。
- `MomentsService` / `AlbumService`：权限与业务规则；`MomentsNotifier`：SignalR `PostChanged` / `AlbumChanged` 与评论、点赞的 Web Push。
- 那年今天与按月相册复用 `GalleryService` 的"我能看到的照片"集合，实时计算。
