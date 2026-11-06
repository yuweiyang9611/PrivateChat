# Drive（网盘与共享）— 待实现

对应 docs/DESIGN.md 4.4 / 4.6。计划包含：

- `BlobsController`：`POST /api/blobs/precheck`（按 sha256 秒传）、`GET /api/blobs/{id}?v=`（Cookie 鉴权、Range、ETag、immutable 缓存；权限 = 任意引用该 blob 的消息 / 节点 / 动态可读）。
- `UploadsController` / tusdotnet 中间件：`/api/uploads`，8 MB 分片，完成回调校验 sha256 → `IBlobStore.MoveInAsync` → 写 blobs → 入队派生任务。
- `DriveController`：spaces、nodes/children、新建 / 重命名 / 移动 / 删除、shares、gallery（按 taken_at 聚合）、trash / restore。
- `ShareLinkController`：`GET /s/{token}`（密码 / 过期）。
- `MediaJobs`：缩略图（Magick.NET，HEIC）、视频封面与 play-720p 转码（ffmpeg）、语音转 m4a；由 jobs 表驱动。
- 后期：WebDAV（NWebDav）。
