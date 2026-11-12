# Remote（远程控制，被控端 Windows）

对应 docs/DESIGN.md 4.8，帧格式见 `PrivateChat.Contracts/RemoteControl.cs`（浏览器那份在 `web/src/features/remote/protocol.ts`）。

- `RemoteService`：权限（主人 / 白名单里的成年人能控，主人 / 管理员能管）、设备列表与策略（改名、白名单、无人值守 PIN）、吊销、
  会话状态机（Pending → Active → Ended，带条件的 UPDATE）、PIN 校验（设备行锁里做，连错 5 次锁 15 分钟）、审计列表、维护（等同意超时、一端断开超过 30 秒）。
- `DevicesController`（`/api/devices`）、`RemoteSessionsController`（`/api/remote/sessions`）、`RemoteRelayController`（`/ws/remote/{id}/{agent|controller}`）。
- `AgentHub`（`/hubs/agent`，只认设备令牌）+ `AgentRegistry`（在线的 Agent，每台设备一条连接，显示器信息只在内存里）。
- `RemoteRelay`：两条 WebSocket 配对、按消息原样转发；只看模式下丢掉控制端的输入帧；对端上下线发文本通知；同角色重连顶掉旧连接（4000），会话结束 4001。
- `RemoteNotifier`：网页 `RemoteSessionChanged`（控制的人 + 设备主人）、`DeviceChanged`（所有人，各自重新拉列表）；Agent 的 `SessionRequested` / `SessionEnded` / `Revoked`。
- 设备码登记在 `Features/Auth/AuthController`（`device/code` · `poll` · `lookup` · `confirm`）；设备令牌只能用于上面这些远程接口（`DeviceTokenAuthenticationHandler`）。
- 被控端程序在 `src/PrivateChat.Agent`（WinForms 托盘：GDI 截屏 + Media Foundation H.264 + SendInput），web 镜像构建时交叉编译成单文件 exe 放在 `/downloads/`。

测试：`tests/.../Integration/RemoteTests.cs`（真实 PostgreSQL + 测试服务器上的 WebSocket），`web/e2e/remote.cjs`（Edge + 本机无界面 Agent，测试画面、键鼠写日志）。
