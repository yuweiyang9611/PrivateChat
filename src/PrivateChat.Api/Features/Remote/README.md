# Remote（远程控制，被控端 Windows）— 待实现

对应 docs/DESIGN.md 4.8，帧格式见 PrivateChat.Contracts/RemoteControl.cs。计划包含：

- `DevicesController`：`GET /api/devices`、`PATCH /api/devices/{id}`（名称、allowed_controllers、无人值守 PIN）、`DELETE`（吊销设备令牌）。
- `RemoteSessionsController`：`POST /api/remote/sessions`（校验 Admin/Member + 白名单 → SignalR 通知 Agent）、`/{id}/consent`、`/{id}/end`、`GET`（审计）。
- `RemoteRelayMiddleware`：原生 WebSocket `/ws/remote/{sessionId}/{agent|controller}`，字节级透传，不解析不落盘；每 15 s 心跳（Cloudflare 空闲 100 s 断开）。
- 设备码登录已在 Features/Auth/AuthController（device/code、poll、confirm）。
- Agent 侧（PrivateChat.Agent）：ffmpeg ddagrab + libx264 zerolatency 子进程采集编码、SendInput 注入、托盘与同意弹窗。
