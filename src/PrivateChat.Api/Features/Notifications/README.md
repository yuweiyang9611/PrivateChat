# Notifications（通知与 Web Push）— 待实现

对应 docs/DESIGN.md 4.10。计划包含：

- `NotificationsController`：`GET /api/notifications`、`POST /api/notifications/read`。
- `PushController`：`POST/DELETE /api/push/subscribe`（订阅存 devices.push_subscription）。
- `WebPushSender`：VAPID 密钥来自 Infrastructure/Keys/KeyStore（首次启动自动生成）；离线成员去抖 3 s 推送，静音会话 / 夜间勿扰不推。
- 来电推送：Chrome / 安卓带接听 / 拒绝 actions，iOS 仅点击打开。
