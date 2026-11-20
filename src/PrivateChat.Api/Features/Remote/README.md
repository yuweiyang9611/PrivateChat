# Remote（远程控制，被控端 Windows）

对应 docs/DESIGN.md 4.8，帧格式见 `PrivateChat.Contracts/RemoteControl.cs`（浏览器那份在 `web/src/features/remote/protocol.ts`）。

- `RemoteService`：权限（主人 / 白名单里的成年人能控，主人 / 管理员能管）、设备列表与策略（改名、白名单、免同意名单、无人值守 PIN）、吊销、
  会话状态机（Pending → Active → Ended，带条件的 UPDATE）、PIN 校验（设备行锁里做，连错 5 次锁 15 分钟）、免同意名单（名单里的人直接 Active，记为 Trusted；对 Agent 仍说 Unattended）、审计列表、维护（等同意超时、一端断开超过 30 秒）。
- `DevicesController`（`/api/devices`）、`RemoteSessionsController`（`/api/remote/sessions`）、`RemoteRelayController`（`/ws/remote/{id}/{agent|controller}`）。
- `AgentHub`（`/hubs/agent`，只认设备令牌）+ `AgentRegistry`（在线的 Agent，每台设备一条连接，显示器信息只在内存里）。
- `RemoteRelay`：两条 WebSocket 配对、按消息原样转发；只看模式下丢掉控制端的输入帧；对端上下线发文本通知；同角色重连顶掉旧连接（4000），会话结束 4001。
- `RemoteNotifier`：网页 `RemoteSessionChanged`（控制的人 + 设备主人）、`DeviceChanged`（所有人，各自重新拉列表）；Agent 的 `SessionRequested` / `SessionEnded` / `Revoked`。
- 设备码登记在 `Features/Auth/AuthController`（`device/code` · `poll` · `lookup` · `confirm`）；设备令牌只能用于上面这些远程接口（`DeviceTokenAuthenticationHandler`）。
- 被控端程序在 `src/PrivateChat.Agent`（WinForms 托盘：GDI 截屏 + Media Foundation H.264 + SendInput），web 镜像构建时交叉编译成单文件 exe 放在 `/downloads/`。
  安装包旁边有一份清单 `/downloads/PrivateChat.Agent.json`（版本、大小、SHA-256，镜像构建时生成）：已经装出去的程序（0.6.2 起）靠它自动升级（`Updater`）。
  系统没有 TLS 1.3（Windows 10 及更早）时它用程序自带的 TLS 1.3 连服务器（`AgentTransport`，BouncyCastle；证书仍由 Windows 校验），所以服务器可以只收 TLS 1.3。

Agent 和网页通过同一个中继传送画面、反馈和系统声音。Agent 的 `NetworkEstimator` 根据画面确认和探测包往返时间调整发送预算，`QualityPlanner` 根据画面变化选择 H.264 编码质量。网页开启声音后，Agent 从 Windows 默认播放设备采集并发送 Opus，关闭后释放设备。中继按原有转发规则处理这些帧。

快速线路（被控端 0.8.0 起）：画面和声音可以不经中继，经 Cloudflare Realtime 从被控端到网页（键鼠等控制帧仍走中继）。`RemoteRealtimeService` 做信令：被控端 `…/realtime/publish` 交 offer 换 answer，网页 `…/realtime/pull` 拿 offer、`…/realtime/renegotiate` 交 answer，两边都先 `GET …/realtime` 问能不能用和 ICE 服务器。复用通话的 `CloudflareRealtimeClient`，Cloudflare 那边的会话和轨道记在内存里的 `RemoteRealtimeStore`，会话结束时经 `CallCleanupQueue` 关掉。被控端那一半在 `src/PrivateChat.Agent/Realtime`（`RealtimePublisher`、`H264Rtp`）和 `Session/RealtimeRateControl.cs`，网页那一半在 `web/src/features/remote/RemoteRealtime.ts`。设计见 docs/DESIGN.md 4.8 的"快速线路"。

**验证入口**

- `tests/PrivateChat.Api.Tests/Integration/RemoteTests.cs` 使用 PostgreSQL 和测试服务器的 WebSocket 验证会话与中继。`RemoteRateControlTests.cs` 覆盖弱网、慢解码、画质恢复和音量处理。
- `web/e2e/remote.cjs` 使用 Edge 和本机无界面 Agent 验证测试画面、键鼠事件和 440 Hz 测试音，包含静音和恢复播放。
- `RemoteTests` 里的 `Fast_line_*` 用 `FakeRealtime` 验证快速线路的接口、权限和清理；`H264RtpTests`、`RealtimeRateControlTests` 覆盖 RTP 分片和码率控制。`node e2e/remote.cjs --realtime` 用假 SFU（Edge）跑快速线路的完整流程。
- 在 Windows 上启动测试 Compose 栈后，从 `web` 目录运行 `node e2e/remote-weak.cjs`，可测量三种模拟线路的实际帧率、延迟和最长画面停顿。`npm run e2e` 也会运行这组测试。
