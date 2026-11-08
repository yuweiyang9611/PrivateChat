# PrivateChat · 家庭私有通讯平台设计稿

| | |
|---|---|
| 版本 | v0.1（讨论稿） |
| 日期 | 2026-11-05 |
| 状态 | 设计阶段，仓库尚无代码 |
| 技术栈 | .NET 10 · ASP.NET Core · SignalR · PostgreSQL 17 · Cloudflare Realtime · Vue 3 · TypeScript · Element Plus · Docker Compose |

---

## 0. 结论先行

1. **后端做成模块化单体**：一个 ASP.NET Core 10 进程承载 REST、SignalR、WebSocket 中继和后台媒体任务；前端静态文件由 nginx 托管。家庭规模不需要微服务，但代码按功能"垂直切片"组织，将来拆得开。
2. **音视频通话/会议用 Cloudflare Realtime SFU + TURN**：媒体 100% 走 Cloudflare，家里不跑媒体服务器、不开任何 UDP 端口；.NET 负责房间编排（调用会话 / 轨道 API）和通话记录。
3. **远程控制必须有原生 Agent**：浏览器只能共享屏幕，无法注入键鼠。用 .NET 写 Windows 被控端 Agent（被控电脑都是 Windows）；传输先走服务器 WebSocket 中继（443 一个端口通吃、不怕 NAT），浏览器用 WebCodecs 解码。v1 Agent 用 ffmpeg 子进程采集编码，几天就能跑起来。
4. **存储用本地磁盘卷 + 内容寻址 Blob 表**：聊天附件、网盘文件、动态图片三处引用同一份数据，按 SHA-256 去重；通过 `IBlobStore` 抽象，日后可换 S3。
5. **前端一套 Vue 3 + TS 代码**覆盖桌面浏览器和手机 PWA（可安装、可推送），后期如需后台来电再用 Capacitor 套壳。
6. **入口走 Cloudflare Zero Trust**：服务器不开任何入站端口，cloudflared 建出站隧道，TLS 在 Cloudflare 边缘终结，Cloudflare Access 做身份门禁，应用直接信任 Access 签发的身份、不再维护密码。nginx 托管前端静态文件并反代 API。必需容器 4 个（cloudflared、web、api、postgres），`docker compose up -d` 一键。

---

## 1. 目标与范围

### 1.1 使用场景

- 一个家庭 5–20 人，可能跨城市 / 跨国；设备混杂：iPhone、安卓、Windows、Mac、长辈的平板。
- 多数成员非技术用户，"安装"门槛要尽量低：浏览器打开即用、添加到主屏幕即可。
- 部署在自家 NAS / 小主机 / 一台云服务器上，由家里的"技术担当"维护。

### 1.2 功能范围与优先级

| 优先级 | 功能 | 说明 |
|---|---|---|
| P0 | 账号、家庭、邀请码、角色 | 关闭公开注册 |
| P0 | 文字 / 图片 / 语音 / 视频 / 文件消息，群聊，已读，撤回，表情回应，引用回复 | 核心 |
| P0 | 离线推送（Web Push）、PWA | 收不到消息就没人用 |
| P1 | 云盘：断点续传上传、目录、共享、回收站、相册时间线 | 与聊天共用存储 |
| P1 | 1:1 音视频通话、多人会议、屏幕共享、常驻"客厅"会议室 | Cloudflare Realtime |
| P2 | 动态（SNS）：发动态、评论、点赞、相册、那年今天 | 家庭朋友圈 |
| P2 | 远程控制（被控端为 Windows） | 帮爸妈修电脑 |
| P3 | WebDAV 挂载、搜索、长辈模式、原生 App 壳 | 打磨 |

### 1.3 明确不做

- **端到端加密**：服务器是自家的，且缩略图、转码、搜索都需要明文。后期可为 1:1 对话单独加 MLS，架构不排斥。
- 公开注册、多租户、商业化。
- 自研 SFU、自研 TURN、自研视频编解码。
- 横向扩展：单机即可。SignalR 若将来多实例只需加 Redis backplane，协议不变。

---

## 2. 技术选型

### 2.1 总表

| 层 | 选择 | 理由 | 备选 |
|---|---|---|---|
| 运行时 | .NET 10（LTS，支持到 2028-11） | 本机已装 SDK 10.0.403 | .NET 9 |
| Web 框架 | ASP.NET Core MVC Controller（`[ApiController]` + 特性路由）+ SignalR | 按资源分组清晰，自动模型校验、过滤器做统一异常与审计；实时推送内置 | Minimal API |
| ORM / 数据库 | EF Core 10 + PostgreSQL 17 | JSONB 存消息载荷；`pg_trgm` 做中文子串搜索；只要一个容器 | SQLite（并发写弱）、MySQL |
| 对象存储 | 本地磁盘卷 + 内容寻址 + `IBlobStore` 抽象 | 零额外组件，ffmpeg 直接读，备份就是拷目录 | S3 兼容（Garage / RustFS / 云 OSS） |
| 大文件上传 | tus 协议（`tusdotnet` + `tus-js-client`） | 断点续传，手机弱网必需 | 自研分片 |
| 媒体处理 | ffmpeg（容器内）+ Magick.NET | 视频转码 / 封面；HEIC → JPEG、EXIF 方向 | ImageSharp（商用授权限制） |
| 音视频 | Cloudflare Realtime（SFU + TURN，REST API 编排） | 媒体 100% 走 Cloudflare，家里零媒体端口、零媒体带宽；每月前 1 TB 免费 | LiveKit 自建（需公网端口或 WARP）、RealtimeKit 托管 SDK |
| 远程控制 | 自研 .NET Agent + WebSocket 中继 + 浏览器 WebCodecs 解码 | 全程走 443，无 NAT 问题 | RustDesk 自托管（兜底）、WebRTC P2P（v3） |
| 推送 | Web Push（VAPID） | iOS 16.4+ / Android / 桌面全覆盖，无需厂商通道 | Capacitor 原生推送（后期） |
| 认证 | Cloudflare Access 签发的身份（JWT 头）→ 应用按邮箱自动建号；Agent 用 Access 服务令牌 + 应用设备令牌 | 身份、验证码、多因素都交给 Cloudflare，应用不存密码 | 应用内密码登录（局域网直连备用） |
| API 契约 | 内置 OpenAPI + Scalar → `openapi-typescript` / `openapi-fetch` | 端到端类型安全 | 手写 DTO |
| 前端 | Vue 3.5 + TypeScript 5 + Vite 7 + Pinia + Vue Router + VueUse | 用户指定 | — |
| UI 库 | Element Plus + Tailwind CSS 4 | 组件全、中文文档与社区成熟、unplugin 按需自动导入 | Naive UI |
| 本地缓存 | Dexie（IndexedDB） | 消息离线缓存、冷启动秒开 | — |
| PWA | vite-plugin-pwa | 手机"安装"、推送、Share Target | Capacitor（后期套壳） |
| 入口 | Cloudflare Tunnel（cloudflared）+ Cloudflare Access | 零入站端口、零证书管理、自带 WAF 与身份门禁 | Caddy / Traefik 自签证书直连 |
| 前端托管 / 反代 | nginx | 静态文件 + 反代 API / SignalR；内网明文 HTTP，TLS 在 Cloudflare 边缘 | Caddy |
| 部署 | Docker Compose（4 个必需容器） | 一键 | k3s（过度） |

### 2.2 三个关键决策

#### 决策 A：音视频用 Cloudflare Realtime SFU，不自建

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| 浏览器 P2P mesh | 零服务端 | N 人会议每人要上传 N−1 路，手机 4 人就卡；无法录制 | 仅适合 1:1，不选 |
| **Cloudflare Realtime**（SFU + TURN） | 媒体 100% 走 Cloudflare 任播网络，家里零媒体端口、零媒体带宽；与 Tunnel / Access 同一个账号；每月前 1 TB 免费 | 没有"房间"概念，要自己编排会话与轨道；无服务端录制；产品较新 | **选** |
| LiveKit 自建 | 功能最全、SDK 成熟 | 媒体 UDP 过不了 Cloudflare 隧道，必须开公网端口或让家人装 WARP，与"全走 Cloudflare"冲突 | 不选 |
| Jitsi Meet | 功能全 | 5+ 容器，同样要开媒体端口 | 不选 |
| RealtimeKit（Cloudflare 托管 SDK） | 几乎不用写通话代码 | 按分钟计费、UI 难深度定制 | 备选 |

.NET 侧做的事：按参与者创建 Cloudflare 会话、转发 SDP、增删轨道、签发 TURN 凭据、维护房间与参与者状态并经 SignalR 广播。浏览器只用原生 `RTCPeerConnection`，App Secret 永远不下发。

#### 决策 B：存储用本地磁盘 + 内容寻址，不上对象存储

- MinIO 社区版 2025 年起转为仅提供源码、不再发布预编译二进制与镜像，已不是"随手拉一个"的组件；Garage / RustFS / SeaweedFS 可用，但对家庭场景是多余的一层。
- 家庭规模（几百 GB 到几 TB 照片视频）一块盘或 NAS 卷足够，备份就是 rsync / restic 一个目录。
- 代码里定义 `IBlobStore`（Put / OpenRead / Delete / Exists），v1 实现 `FileSystemBlobStore`，路径为 `/data/blobs/{sha256 前 2 位}/{次 2 位}/{sha256}`；将来 `S3BlobStore` 可无痛替换。
- 所有媒体按 SHA-256 去重，形成"**一份存储，三处引用**"：同一张照片在聊天里发过、又放进网盘、又发了动态，磁盘上只有一份。

#### 决策 C：远程控制用"Agent + 服务器中继"，不用 P2P，也不可能纯浏览器

- 浏览器只能 `getDisplayMedia` 共享屏幕，**无法注入键鼠**，所以被控端必须装原生程序。
- WebRTC P2P 需要 STUN / TURN；国内手机网络多为对称 NAT / CGNAT，大概率还是要走 TURN 中继。既然都要中继，不如直接用自家服务器做 WebSocket 中继：**443 一个端口通吃**，穿墙无忧，实现最简单。
- 视频流：Agent 产出 H.264 Annex-B 裸流 → WebSocket → 浏览器 `VideoDecoder`（WebCodecs）→ canvas。局域网延迟 <100 ms，公网 150–300 ms，足够"帮爸妈点鼠标、装软件"。
- 分三步走：v1 Agent 用 ffmpeg 子进程（`ddagrab` 采集 + libx264 zerolatency），零 native 互操作；v2 进程内采集 + 硬件编码，解决 UAC / 锁屏；v3 可选 WebRTC P2P 再降延迟。
- RustDesk 自托管作为过渡与兜底（compose 里一个可选 profile），网页里记录成员的 RustDesk ID 一键唤起。
- 入口换成 Cloudflare Tunnel 后这个选择更顺：隧道只代理 HTTP / WebSocket，中继方案原样可用；WebRTC P2P 反而要另开端口。

---

## 3. 总体架构

### 3.1 组件图

```mermaid
flowchart LR
  subgraph clients["家庭成员设备"]
    web["Vue 3 PWA<br/>桌面 / 手机浏览器"]
    agent["远程控制 Agent<br/>.NET 10 托盘程序"]
  end
  subgraph cf["Cloudflare"]
    edge["边缘：TLS 终结 · WAF<br/>Access 身份门禁"]
    rt["Realtime SFU + TURN<br/>通话媒体"]
  end
  subgraph host["家庭服务器 · docker compose（零入站端口）"]
    cfd["cloudflared<br/>出站隧道"]
    nginx["nginx<br/>静态前端 · 反代"]
    api["PrivateChat.Api<br/>REST · SignalR · WS 中继<br/>后台媒体任务 · 通话编排"]
    pg[("PostgreSQL 17")]
    blobs[("Blob 卷<br/>/data/blobs · /data/derived")]
  end
  web -- "HTTPS / WSS" --> edge
  agent -- "HTTPS / WSS + 服务令牌" --> edge
  web -- "WebRTC 媒体" --> rt
  edge -- "隧道（由服务器出站建立）" --> cfd
  cfd --> nginx
  nginx -- "/api /hubs /ws /s" --> api
  api -. "REST：会话 / 轨道 / TURN 凭据（出站）" .-> rt
  api --> pg
  api --> blobs
```

### 3.2 运行时组件

| 组件 | 容器 | 职责 |
|---|---|---|
| Cloudflare 边缘 | 无（云端） | TLS 终结、证书、WAF、Access 身份门禁；服务器不需要任何证书 |
| Cloudflare Realtime | 无（云端） | SFU + TURN，通话媒体不进家庭网络；api 通过 REST 创建会话、增删轨道、签发 TURN 凭据 |
| cloudflared | `cloudflared` | 向 Cloudflare 建出站隧道，把 `chat.example.com` 的流量送到 nginx |
| nginx | `web` | 托管 Vue 静态文件；反代 `/api` `/hubs` `/ws` `/s` 到 api |
| PrivateChat.Api | `api` | REST（Controller）、SignalR、远程控制 WebSocket 中继、通话编排、后台媒体任务（同进程 HostedService） |
| PostgreSQL | `postgres` | 全部结构化数据 |
| 数据卷 | `data` | `/data/blobs` 原件、`/data/derived` 缩略图与转码产物、`/data/keys` 自动生成的密钥、`/data/tus` 上传临时区 |
| Agent | 不在容器内 | 安装在家庭成员电脑上的托盘程序，带 Access 服务令牌经隧道连回服务器 |

### 3.3 三条核心数据流

**发一张照片**

1. 前端用 Web Crypto 流式算出 SHA-256，`POST /api/blobs/precheck`；已存在则秒传拿到 blobId。
2. 否则走 tus 协议分片上传到 `/api/uploads`（断网可续），完成后服务端校验哈希、落盘、写 `blobs` 行。
3. 前端通过 SignalR `SendMessage` 发送消息（带 clientMessageId + blobId）；服务端在事务里给会话 seq +1、写 `messages`，向会话成员广播 `MessageReceived`，离线成员入队 Web Push。
4. 后台任务生成 320 / 1280 缩略图（HEIC 转 WebP），完成后广播 `MessageUpdated` 补齐缩略图字段。

**发起视频通话**

1. A 点"视频" → `POST /api/calls` → 创建 `call_sessions`，SignalR `IncomingCall` + Web Push 通知其他成员，60 s 无人接听记为未接。
2. 每个加入者 `POST /api/calls/{id}/join`：api 调 Cloudflare `sessions/new` 建一个会话（存入 `call_participants.cf_session_id`），并返回 24 h 有效的 TURN 凭据；浏览器用原生 `RTCPeerConnection` 建连，媒体直接到 Cloudflare 的任播节点，不经过家里。
3. 发布：浏览器生成 offer → `POST /api/calls/{id}/tracks` → api 调 `tracks/new` 拿到 answer 回传，写 `call_tracks`，广播 `TrackPublished`。
4. 订阅：其他端收到 `TrackPublished` → `POST /api/calls/{id}/tracks/pull` → Cloudflare 返回带新轨道的 offer → 浏览器回 answer → `POST /api/calls/{id}/renegotiate`。
5. 离开或 SignalR 断线超过 30 s → api 关闭该会话的轨道、广播 `ParticipantLeft`；最后一人离开则结束通话，在会话里插入一条"通话"消息。

**远程控制爸爸的电脑**

1. 爸爸电脑上的 Agent 带 Cloudflare Access 服务令牌穿过门禁，再用设备令牌连接 SignalR，上报在线、分辨率、显示器数。
2. 我在"设备"页点"远程控制" → `POST /api/remote/sessions`；服务端校验角色与设备策略，经 SignalR 通知 Agent。
3. Agent 弹窗"XX 请求远程控制（30 s）"，爸爸点同意；或者我输入该设备的无人值守 PIN。
4. 双方各自连接 `/ws/remote/{sessionId}/agent` 与 `/controller`，服务端做字节级透传；Agent 推 H.264 帧，我这边把鼠标 / 键盘事件发回去。
5. 被控端屏幕顶部有红色常驻条"正在被 XX 远程控制 [断开]"；任一方断开即结束，服务端写审计记录。

---

## 4. 后端设计

### 4.1 代码结构

```
PrivateChat/
├─ PrivateChat.slnx
├─ Directory.Build.props / Directory.Packages.props   # 统一包版本（Central Package Management）
├─ src/
│  ├─ PrivateChat.Api/                 # 唯一的服务进程（模块化单体）
│  │  ├─ Program.cs                    # 组装：AddControllers / MapControllers、Access 认证、SignalR、OpenAPI、tus、HostedService
│  │  ├─ Features/                     # 垂直切片：每个功能自带 Controllers / Hub / Service / Dto
│  │  │  ├─ Auth/            Cloudflare Access 身份映射、局域网密码登录、设备码登录
│  │  │  ├─ Family/          家庭、成员、邀请码、角色
│  │  │  ├─ Chat/            会话、消息、ChatHub、已读、撤回、表情回应
│  │  │  ├─ Calls/           Cloudflare Realtime 会话 / 轨道编排、TURN 凭据、通话记录、"客厅"常驻房间
│  │  │  ├─ Drive/           节点、共享、tus 上传、流式下载、回收站、WebDAV（后期）
│  │  │  ├─ Moments/         动态、评论、点赞、相册、那年今天
│  │  │  ├─ Remote/          设备注册、会话协商、同意流程、WebSocket 中继
│  │  │  ├─ Notifications/   Web Push 订阅与发送、站内通知
│  │  │  └─ Search/          pg_trgm 搜索
│  │  ├─ Domain/             实体与值对象（无框架依赖）
│  │  └─ Infrastructure/     AppDbContext、FileSystemBlobStore、FfmpegMediaProcessor、
│  │                         DbJobQueue、WebPushSender、CloudflareRealtimeClient、CloudflareAccessAuthHandler
│  ├─ PrivateChat.Contracts/           # Api 与 Agent 共享的协议（远程控制帧格式等）
│  └─ PrivateChat.Agent/               # 远程控制 Agent（net10.0-windows，仅 Windows；托盘 + 后台 Worker）
├─ web/                                 # Vue 3 + TS
├─ deploy/                              # docker-compose.yml、nginx.conf、Dockerfile.api / .web、init-env.sh
├─ docs/
└─ tests/
   ├─ PrivateChat.Api.Tests/            # xUnit + Testcontainers（真 Postgres）
   └─ web：vitest + playwright
```

约定：

- 一个 Feature 目录 = 按资源划分的若干 `[ApiController]` 控制器（`[Route("api/[controller]")]`，如 `ConversationsController`、`MessagesController`）+ 服务类。控制器只做参数绑定、鉴权特性和调用服务，业务逻辑全在服务类里；统一异常、审计日志用全局过滤器。跨 Feature 只通过服务接口或领域事件调用，不互相翻对方的表。
- 所有主键用 `Guid.CreateVersion7()`：时间有序，B-Tree 索引友好。
- 时间统一 UTC `timestamptz`，前端按用户时区显示。
- 配置走 Options 模式，环境变量用 `App__PublicUrl` 这种双下划线映射，方便 compose 注入。
- 首次启动自动生成设备令牌签名密钥与 VAPID 密钥对，存到 `/data/keys`，`.env` 里不用填。

### 4.2 数据模型

**身份与家庭**

| 表 | 关键字段 |
|---|---|
| `users` | id, email（Access 身份映射键）, username, display_name, avatar_blob_id, password_hash（仅局域网备用登录）, role（Admin / Member / Child）, elder_mode, created_at, disabled_at |
| `families` | id, name。v1 单家庭，表保留给未来 |
| `invites` | email（允许自动建号的家人邮箱）, code（局域网模式用）, created_by, role, expires_at, used_by |
| `devices` | id, user_id, kind（Browser / Agent）, name, platform, push_subscription jsonb, token_hash, last_seen_at, remote_capable, unattended_pin_hash, allowed_controllers jsonb |
| `refresh_tokens` | id, user_id, device_id, hash, expires_at, revoked_at |

**聊天**

| 表 | 关键字段 |
|---|---|
| `conversations` | id, type（Direct / Group / Family）, title, avatar_blob_id, last_seq, created_at |
| `conversation_members` | conversation_id, user_id, role, last_read_seq, muted_until, joined_at |
| `messages` | id, conversation_id, **seq**, sender_id, client_message_id, type（Text / Image / Video / Audio / File / Sticker / Call / System）, body, payload jsonb, reply_to_id, forwarded_from_id, created_at, edited_at, deleted_at |
| `message_attachments` | message_id, blob_id, kind, meta jsonb（width, height, duration, name, size, thumb / poster 变体） |
| `message_reactions` | message_id, user_id, emoji, created_at |

唯一约束：`(conversation_id, seq)`、`(sender_id, client_message_id)`。索引：`(conversation_id, seq desc)`，`body` 上 `gin_trgm_ops`。

**存储**

| 表 | 关键字段 |
|---|---|
| `blobs` | id, sha256（唯一）, size, mime, storage_key, created_by, created_at |
| `derived_blobs` | blob_id, variant（thumb-320 / thumb-1280 / poster / play-720p / audio-m4a）, storage_key, meta jsonb, status |

**网盘**

| 表 | 关键字段 |
|---|---|
| `spaces` | id, type（Personal / Family）, owner_user_id, quota_bytes |
| `drive_nodes` | id, space_id, parent_id, name, is_folder, blob_id, size, mime, created_by, created_at, updated_at, deleted_at |
| `node_shares` | id, node_id, grantee_user_id（可空）, link_token（可空）, permission（Read / Write）, password_hash, expires_at |

同一目录下文件名唯一（排除已删除）。

**动态**

| 表 | 关键字段 |
|---|---|
| `posts` | id, author_id, text, visibility（Family / Private）, created_at, deleted_at |
| `post_media` | post_id, blob_id, sort, meta jsonb |
| `comments` | id, post_id, author_id, reply_to_comment_id, text, created_at, deleted_at |
| `reactions` | target_type（Post / Comment）, target_id, user_id, emoji |
| `albums` / `album_items` | 相册标题、封面；条目引用 blob，带 taken_at |

**通话与远程**

| 表 | 关键字段 |
|---|---|
| `call_sessions` | id, conversation_id, type（Audio / Video / Meeting）, started_by, started_at, ended_at, scheduled_at, egress_bytes_estimate |
| `call_participants` | id, call_id, user_id, cf_session_id（Cloudflare 会话）, joined_at, left_at |
| `call_tracks` | id, participant_id, track_name, kind（Audio / Video / Screen）, published_at, unpublished_at |
| `remote_sessions` | id, controller_user_id, target_device_id, mode（View / Control）, consent（Prompt / Unattended）, started_at, ended_at, end_reason, bytes_relayed |

**通知与任务**

| 表 | 关键字段 |
|---|---|
| `notifications` | id, user_id, type, payload jsonb, created_at, read_at |
| `jobs` | id, type, payload jsonb, status, attempts, run_after, locked_until, last_error |

### 4.3 实时通信（SignalR）

Hub 路径 `/hubs/chat`，浏览器走 Cookie 认证，Agent 走 Bearer。

- 连接建立时服务端把连接加入 `user:{id}` 组和该用户所有 `conv:{id}` 组，并标记在线；客户端随后调用 `Sync` 补拉缺失消息。
- 客户端 → 服务端：`SendMessage(dto)`、`Typing(conversationId)`、`MarkRead(conversationId, seq)`、`Sync(Dictionary<convId, lastSeq>)`。
- 服务端 → 客户端：`MessageReceived`、`MessageUpdated`、`MessageDeleted`、`ReactionChanged`、`ReadReceipt`、`Typing`、`Presence`、`ConversationUpdated`、`IncomingCall`、`CallUpdated`、`ParticipantJoined`、`ParticipantLeft`、`TrackPublished`、`TrackUnpublished`、`DriveChanged`、`Notification`、`RemoteSessionChanged`。

可靠性约定：

- **幂等**：客户端为每条消息生成 `clientMessageId`（GUID v7），重发不会重复入库。
- **顺序**：`conversations.last_seq` 在同一事务里 `UPDATE … RETURNING` 自增，`messages.seq` 严格递增；客户端按 seq 排序、按 seq 补洞。
- **重连**：`withAutomaticReconnect` 后调用 `Sync`，服务端返回每个会话 `since seq` 之后的消息（上限 500 条 / 会话，超过提示整页刷新）。
- **离线推送**：收件人没有活跃连接时入队 Web Push，去抖 3 s，静音会话不推。
- 单实例不需要 Redis backplane。
- Cloudflare 对空闲连接约 100 s 超时，SignalR 默认 15 s 一次 keep-alive 已覆盖；远程控制中继的 WebSocket 也要每 15 s 发一次心跳帧。

### 4.4 文件与媒体管道

上传：

1. 客户端先算 SHA-256 → `POST /api/blobs/precheck {sha256, size}`：已存在则秒传返回 blobId。
2. 否则 tus 上传到 `/api/uploads`（`tusdotnet`，分片 8 MB，元数据带 sha256 / 文件名 / mime / 用途）。Cloudflare 免费版单个请求体上限 100 MB，分片保持 8 MB 即可，大文件靠多次 PATCH。
3. 上传完成回调里校验哈希、移入 `/data/blobs/{ab}/{cd}/{sha256}`、写 `blobs` 行、入队派生任务。

派生产物（后台任务）：

- **图片**：Magick.NET 读取（含 HEIC / HEIF、EXIF 方向；是否去除 GPS 可配置）→ WebP 320 / 1280 两档缩略图，原图保留。
- **视频**：ffprobe 取元数据（宽高按旋转纠正、时长、拍摄时间优先用 `com.apple.quicktime.creationdate`）；ffmpeg 在 1 秒处抽帧生成与图片相同的 320 / 1280 封面缩略图；若不是"MP4 / MOV 容器 + H.264 + 8 bit 4:2:0 + 长边 ≤ 1920"，生成 `play-720p`（长边 1280）H.264 / AAC MP4（`-movflags +faststart`），否则删掉这条登记、直接播原件。iPhone 的 HEVC / HDR 视频靠这一步才能在安卓和桌面浏览器播放。
- **两条队列**：快队列（探测、缩略图、视频封面、音频转码，秒级）与转码队列（视频转 720p），各自串行。快的部分做完先保存并广播（封面、时长马上可见），转码完成再广播一次；一段长视频在转码时，后面的照片和语音消息不用等。ffmpeg 超时按失败记下，不会每次重启重试。只补做了媒体信息（消息里看不到变化）时不广播 MessageUpdated；前端收到 MessageUpdated 只更新本地已有的消息，不插入。
- **媒体信息**：图片读 EXIF 的 DateTimeOriginal（有 OffsetTimeOriginal 就用，否则按家庭时区 `App:TimeZone`，默认 Asia/Shanghai），宽高按 EXIF 方向纠正；写入 `blobs.taken_at / width / height / duration_seconds`。启动时补做存量。
- **语音**：浏览器 MediaRecorder 的产物在 Chrome 是 webm / opus，在 Safari 是 mp4 / aac，统一转为 m4a（AAC）并生成波形峰值 JSON。
- 超大视频可选 HLS 切片（后期）。

下载与访问控制：

- `GET /api/blobs/{id}?v=thumb-320`：同源 Cookie 鉴权，`<img>` / `<video>` 标签直接可用，前端不用管 token。产物未就绪时：图片缩略图、`play-720p`、`audio-m4a` 回退原件（`no-store`，生成后经 MessageUpdated / DriveChanged 让前端重取）；视频缩略图返回 404（不能拿原视频当图片）。已确定不会有这一档时（视频探测过、不需要转码；本来就是 m4a 的音频），回退原件照常长期缓存。
- 权限判定：用户对任意引用该 blob 的消息 / 网盘节点 / 动态有读权限即可读。
- 支持 Range、ETag，`Cache-Control: private, max-age=31536000, immutable`（内容寻址，永不变）。
- 外部分享链接 `/s/{token}` 走 `node_shares` 校验，可带密码与过期时间。

配额与回收：每用户配额可选；回收站保留 30 天；夜间 GC 删除引用数为 0 且创建超过 24 h 的 blob。

限制：nginx `client_max_body_size 64m`，Kestrel `MaxRequestBodySize` 同样放宽到 64 MB，两者都大于分片大小、小于 Cloudflare 的 100 MB 上限。

### 4.5 通话与会议（Cloudflare Realtime）

**模型**：Cloudflare Realtime SFU 没有"房间"概念，只有会话（session，对应一个 PeerConnection）和轨道（track）。房间、成员、谁该拉谁的轨道全部由 api 维护，这正是 `call_sessions` / `call_participants` / `call_tracks` 三张表的用途；状态变化通过 SignalR 广播。

**接入方式**：浏览器永远不拿 App Secret，所有 Cloudflare REST 调用由 api（`CallsController` → `CloudflareRealtimeClient`）转发。

- 加入：`POST /api/calls/{id}/join` → api 调 `sessions/new` 为该参与者建一个 Cloudflare 会话，把 sessionId 存进 `call_participants`，同时用 TURN 密钥签发 24 h 有效的凭据（`turn/keys/{keyId}/credentials/generate`）一并返回。
- 发布：前端 `addTransceiver` 后生成 offer → `POST /api/calls/{id}/tracks`（location = local，带 mid / trackName）→ api 调 `sessions/{sid}/tracks/new` → 回传 answer；api 写 `call_tracks` 并广播 `TrackPublished`。
- 订阅：其他端收到 `TrackPublished` → `POST /api/calls/{id}/tracks/pull`（location = remote，带对方 sessionId + trackName）→ Cloudflare 返回新的 offer（requiresImmediateRenegotiation）→ 前端 `setRemoteDescription`、生成 answer → `POST /api/calls/{id}/renegotiate` → api 调 `sessions/{sid}/renegotiate`。
- 取消发布 / 离开：`tracks/close`；离开或 SignalR 断线超时 30 s → api 标记 left、广播 `ParticipantLeft`，最后一人离开则结束通话、插入 `Call` 类型消息。
- 每个参与者一个会话：发布自己的音视频（屏幕共享是第三条轨道），拉取其他所有人的轨道；6 人会议每人拉 5 路。

**呼叫**：`POST /api/calls` 创建记录 → SignalR `IncomingCall` + Web Push（Chrome / 安卓带接听 / 拒绝按钮，iOS 仅点击打开）；60 s 无人接听标记 Missed。常驻会议室"客厅"是一条永不结束的 `call_sessions` 记录，随时可进。

**质量**：开启 simulcast（按当前官方文档确认支持的层数），手机端默认 720p 上行；订阅端按窗口大小请求层。会中聊天复用会话消息。

**没有的东西**：服务端录制（Realtime SFU 不提供，RealtimeKit 才有）、webhook（参与者状态靠 api 自己维护，以 SignalR 连接为准）。

**成本**：媒体出站每月前 1 TB 免费，之后 0.05 美元 / GB。6 人 720p 会议约 13 GB / 小时，1 TB 够每月 70 多小时；api 按通话时长与轨道数估算用量，管理页展示。

### 4.6 网盘与共享

- 两类空间：个人空间（每人一个）与家庭共享空间（全员可写，Child 可配置为只读）。首次访问网盘时自动创建。
- 节点树用 `parent_id` 邻接表，路径由服务端拼；移动 / 重命名只改一行（跨空间移动时整棵子树换 `space_id`）。同一目录（含空间根目录）下名称唯一，新建 / 上传重名自动编号"照片 (1).jpg"，改名重名返回 409。
- **权限**：空间级——个人空间主人 Owner、家庭空间成员 Write（孩子在只读设置下 Read）；节点级——共享给某人的节点及其整棵子树 Read / Write。修改一个文件夹的内容要对**这个文件夹**有写权限，所以被共享的人能改共享文件夹里面的东西，但不能删除 / 改名 / 移走共享文件夹本身；管理共享、跨空间移动需要空间级写权限（被共享的人不能转共享）。
- 共享三种方式：对成员共享（Read / Write）、生成链接（256 bit token，可密码 / 1–365 天过期，只读，孩子不能建）、"发送到聊天"（创建引用同一 blob 的消息，零拷贝）；反过来聊天里的附件也能"保存到网盘"。
- 外链页 `/s/{token}` 由 api 直接渲染极简 HTML，不依赖前端应用：密码解锁后发 12 小时、路径限定的加密 Cookie，密码错误 15 分钟超过 10 次锁定；只能访问共享节点及其子树；严格 CSP、noindex、no-referrer。
- 回收站：删除时整棵子树标记 `deleted_at` 与 `trash_root_id`（这次删除的根），回收站只列根；还原回原位置（原文件夹已不在则回到空间根目录，重名自动编号）；30 天后整批彻底删除。同名覆盖（Replace）时旧文件进回收站。管理回收站项目要对所在空间有空间级写权限；自己删的，也要现在对原来所在的文件夹仍有写权限（共享收回后就不能再还原 / 清除）。跨空间移动文件夹时，里面已经删进回收站的东西留在原空间。
- **并发**：同一空间的目录树修改（新建、上传登记、改名移动、删除、还原、彻底删除）在事务里先拿该空间的 advisory lock，再做存在性、权限、配额检查；跨空间移动按 id 顺序拿两把锁。不会出现挂在已删除文件夹下的活节点，配额也不会被并发上传绕过。
- 配额：空间可设 `quota_bytes`（管理员），回收站里的也计入，超出返回 413。
- **内容持有凭证**（`blob_claims`）：内容去重后 blob 只有一个创建者；后来上传同一文件（或秒传命中）的人登记凭证，才能在消息 / 网盘里引用它。凭证也是垃圾回收"7 天内还有人要"的依据。
- 垃圾回收：每小时一次，删除没有任何消息 / 网盘节点（含回收站）/ 动态 / 相册 / 头像引用、创建超过 7 天且 7 天内无人登记凭证的 blob 及其派生产物（宽限期留给离线发件箱里传完还没发出的消息）；删除语句内再判断一次引用，避免与并发引用竞争。
- **相册时间线**：按拍摄时间（EXIF / 视频元数据）聚合所有图片与视频 blob，不区分来源。聊天里发的照片也出现在时间线上，这是家庭用户最想要的功能。
- WebDAV（后期，`NWebDav`）：Windows 资源管理器 / Finder / 手机文件 App 直接挂载。
- 聊天文件传输统一走"上传到服务器 → 消息引用"，不做 P2P。局域网大文件直传可后期用 WebRTC DataChannel 实现。

### 4.7 动态（SNS）

- 动态 = 文本 + 至多 9 个媒体 + 可见范围；评论支持回复；表情点赞。
- 那年今天：按 taken_at 的月日聚合历史照片与动态，每天凌晨预计算一次。
- 相册：手动相册 + 自动按月相册。
- 推送：有人评论或点赞你的动态。
- 不做算法推荐流、公开页、关注关系，家庭不需要。

### 4.8 远程控制

**组成**

- Agent（被控端）：.NET 10（`net10.0-windows`），Windows 10 / 11 托盘应用 + 后台 Worker，随用户登录自启。被控电脑全部是 Windows，不做 Linux / macOS。
- 控制端：浏览器页面（Vue），无需安装。
- 服务端 `Remote` 模块：设备注册、会话协商（SignalR）、数据面中继（原生 WebSocket `/ws/remote/{sessionId}/{role}`）。

**设备注册（设备码登录）**

1. Agent 首次启动显示 8 位设备码，轮询 `/api/auth/device/poll`。
2. 用户在网页"设备"页输入设备码确认，Agent 获得长期设备令牌（Windows 用 DPAPI 加密保存）。
3. Agent 每个请求都带 Cloudflare Access 服务令牌头（`CF-Access-Client-Id` / `CF-Access-Client-Secret`，安装时与设备码一起填入），过了门禁再用设备令牌连接 SignalR，上报在线状态、分辨率、显示器数。

**会话建立**

1. 控制端 `POST /api/remote/sessions {targetDeviceId, mode}`。服务端校验：请求者是 Admin / Member，且在目标设备的 `allowed_controllers` 白名单里。
2. 服务端经 SignalR 通知 Agent → Agent 弹窗"XX 请求远程控制（30 s）"；无人值守模式则要求控制端输入设备 PIN。
3. 同意后双方各自连接 `/ws/remote/{sessionId}/agent` 与 `/controller`，服务端做字节级透传，不解析、不落盘。
4. 被控端顶部常驻红色条"正在被 XX 远程控制 [断开]"，本地按 Ctrl+Alt+Shift+Q 立即断开。

**数据面协议**（定义在 `PrivateChat.Contracts`，二进制帧：1 字节类型 + 载荷，详见附录 C）

- Agent → 控制端：`VideoConfig`、`VideoFrame`、`Cursor`、`Clipboard`、`Monitors`、`Stats`。
- 控制端 → Agent：`MouseMove`（归一化 0..1 坐标）、`MouseButton`、`Wheel`、`Key`（物理键码 + 修饰键）、`Clipboard`、`SelectMonitor`、`SetQuality`、`RequestKeyFrame`。
- 控制端用 `VideoDecoder`（H.264）解码到 canvas；`keydown / keyup` 用 `e.code` 映射到 Windows 扫描码；指针事件按远端分辨率缩放。

**Agent v1 实现路径（最快可用）**

- 采集 + 编码：子进程 `ffmpeg -f ddagrab -framerate 20 -i desktop -c:v libx264 -preset ultrafast -tune zerolatency -g 40 -bf 0 -pix_fmt yuv420p -f h264 pipe:1`，随 Agent 打包 ffmpeg.exe；读 stdout 按 NAL 单元切帧发送。
- 画质档位：局域网 1080p / 8 Mbps、公网 720p / 2 Mbps，切换时重启 ffmpeg；每个会话单独起一个 ffmpeg 进程，首帧天然是关键帧。
- 注入：`SendInput` P/Invoke（鼠标绝对坐标按 65535 归一化，键盘用扫描码）。
- 安装与自启：`dotnet publish -r win-x64 --self-contained` 单文件发布 + Inno Setup 安装包，内含 ffmpeg.exe；通过任务计划程序随用户登录启动。v1 以当前用户会话运行而非 Windows 服务，这样 `ddagrab`（需要 Windows 8 以上 + DirectX 11）和 `SendInput` 都直接可用。
- v1 明确接受的限制：无法操作 UAC 提示和锁屏（需要 SYSTEM 服务 + 桌面切换）、多显示器只取一块、无音频。
- v2：进程内采集（Windows.Graphics.Capture / DXGI Desktop Duplication）+ Media Foundation 硬件编码，延迟和 CPU 更好，关键帧可按需；SYSTEM 服务 + 用户会话助手进程解决 UAC / 锁屏。
- v3（可选）：WebRTC P2P（SIPSorcery）+ Cloudflare TURN，公网延迟再降 50–100 ms。

**安全**

- 仅 Admin / Member 可控，Child 不可；设备所有者维护白名单；每次会话审计（谁、何时、多久、流量）。
- 设备令牌可在网页一键吊销；无人值守 PIN 错 5 次锁 15 分钟。
- 中继只转发已协商会话的字节；TLS 全程。

**RustDesk 兜底**：`docker compose --profile rustdesk up -d` 启动 hbbs / hbbr；网页"设备"页可记录成员的 RustDesk ID，一键复制或深链 `rustdesk://`。

### 4.9 认证、授权与安全

**两道门**

1. **Cloudflare Access（外门）**：在 Zero Trust 里给 `chat.example.com` 建一个 Access 应用，策略 = 允许家庭成员邮箱列表，登录方式用一次性邮箱验证码（One-time PIN），有条件再加 Google / GitHub 等。会话时长设为最长的 30 天，家人一个月只需验证一次。非家庭成员连应用的登录页都看不到。
2. **应用身份（内门）**：Access 放行的每个请求都带 `Cf-Access-Jwt-Assertion` 头。api 用 Access 的 JWKS（`https://<team>.cloudflareaccess.com/cdn-cgi/access/certs`）校验签名、`aud`（应用的 AUD Tag）和过期时间，取出邮箱；邮箱在成员表里则直接登录，在 `invites` 表里则自动建号，`ADMIN_EMAIL` 对应的邮箱自动成为 Admin。应用不存密码、不做登录页，SignalR、tus、媒体标签全部靠同源 Cookie（`CF_Authorization`）通过门禁。

**Agent 与非浏览器客户端**

- Access 里创建一个服务令牌（Service Token），并在同一个 Access 应用上加一条 Service Auth 策略。Agent 每个请求带 `CF-Access-Client-Id` / `CF-Access-Client-Secret` 两个头，令牌在安装向导里与设备码一起填入。
- 过了 Access 之后，Agent 再用应用自己的设备令牌认证（Bearer），服务端据此知道"这是爸爸的电脑"。两层都可独立吊销。

**局域网直连备用模式**

- Cloudflare 故障或家里断外网时，可通过 `http://服务器IP:8080` 直连 nginx。此时没有 Access 头，api 回退到应用内密码登录：管理员密码由 `.env` 的 `ADMIN_PASSWORD` 在启动时引导写入，成员密码由管理员在设置页设置。纯 HTTP 下 PWA、推送、摄像头这些"安全上下文"功能不可用，只保证能收发消息和看文件。
- nginx 的 8080 监听会清空 `Cf-Access-Jwt-Assertion` 头；api 侧无论如何都校验 JWT 签名与 `aud`，局域网里伪造不了身份。

**角色与防护**

- 角色：Admin（家长：成员管理、存储、远程策略）、Member、Child（不可远程、不可发外链、可见范围受限）。
- 分享链接 `/s/{token}` 默认只对家庭成员有效。若要分享给家庭外的人，在 Access 里为 `chat.example.com/s` 单独建一个 Bypass 应用，由 ≥128 bit 的随机 token + 可选密码 + 过期时间兜底。
- 其余：CSP、`X-Frame-Options`、上传 MIME 嗅探校验、文件路径只用 id 不用原名；WAF、速率限制、Bot 过滤由 Cloudflare 边缘托管，api 读 `CF-Connecting-IP` 作为真实来源 IP。
- 磁盘加密交给宿主（BitLocker / LUKS），备份用 restic 加密。

### 4.10 通知

- 站内通知表 + SignalR 即时推送；离线 Web Push；每会话免打扰；夜间勿扰按用户时区。
- iOS 只有"添加到主屏幕"的 PWA 才能收推送（16.4+），首次使用时引导。

### 4.11 搜索

- `pg_trgm` GIN 索引覆盖消息正文、文件名、动态文本，中文子串匹配对家庭规模够用。
- 后期可选 Meilisearch（compose profile）。

### 4.12 后台任务

- `jobs` 表 + HostedService 轮询（`FOR UPDATE SKIP LOCKED`）。任务类型：缩略图、转码、语音转码、推送、GC、那年今天预计算。
- 失败重试 3 次指数退避，重启不丢；并发度 2（ffmpeg 吃 CPU），大转码低优先级。

### 4.13 可观测性

- Serilog 输出 stdout（`docker logs`）；`/healthz` 检查数据库、磁盘剩余空间、Cloudflare Realtime API 可达。
- 管理页展示存储用量、任务队列、在线设备；OpenTelemetry + Prometheus 可选。

---

## 5. 前端设计

### 5.1 工程

Vite 7 + Vue 3.5 + TypeScript 5 + Pinia + Vue Router + VueUse + Element Plus（unplugin-vue-components 按需自动导入）+ Tailwind CSS 4 + vue-i18n（zh-CN 默认）。
`openapi-typescript` 从 `/openapi/v1.json` 生成类型，`openapi-fetch` 调用；`@microsoft/signalr`、`tus-js-client`、`dexie`、`photoswipe`；通话直接用浏览器原生 `RTCPeerConnection`，不引入第三方 SDK；vitest 单测，playwright 端到端；eslint + prettier；vite-plugin-pwa。

### 5.2 目录

```
web/src/
├─ api/            openapi-fetch 客户端与生成的类型
├─ realtime/       SignalR 连接管理（单例、重连、Sync）
├─ stores/         session, family, conversations, messages, calls, drive, moments, devices, ui
├─ db/             Dexie：messages、conversations、blob 元数据缓存
├─ features/
│  ├─ chat/        ConversationList、MessageList（虚拟滚动）、Composer、MessageBubble/*、VoiceRecorder
│  ├─ calls/       RealtimeSession（RTCPeerConnection 封装）、CallPage、IncomingCallDialog、ParticipantGrid、Controls、ScreenShare
│  ├─ drive/       Explorer、Uploader（tus）、Previewer、ShareDialog、Gallery
│  ├─ moments/     Feed、PostComposer、PostCard、OnThisDay、Albums
│  ├─ remote/      DeviceList、RemoteViewer（canvas + WebCodecs）、InputCapture
│  ├─ family/      Members、Invites、Roles
│  └─ settings/    Profile、Notifications、ElderMode、Devices
├─ layouts/        DesktopLayout（三栏）、MobileLayout（底部 Tab）
├─ components/     Avatar、MediaViewer、FileIcon、EmojiPicker 等通用组件
└─ router/
```

### 5.3 关键机制

- **消息列表**：以 seq 为 key 的虚拟滚动；先从 IndexedDB 渲染再增量同步，冷启动秒开。
- **发送**：乐观插入（pending 状态）→ 服务端回执替换；失败可重发。
- **媒体**：缩略图懒加载，点开 PhotoSwipe；视频默认播 `play-720p`，长按下载原件。
- **上传**：tus 队列，可暂停、断网自动续传，进度显示在消息气泡里。
- **语音**：MediaRecorder + 波形；移动端按住说话，桌面点按录制。
- **通话**：`RealtimeSession` 封装一个 `RTCPeerConnection`，发布 / 订阅轨道都经 api 转发给 Cloudflare，Pinia store 维护参与者与轨道；来电全局弹窗；会中可最小化为悬浮窗。
- **远程**：`RemoteViewer` 把 NAL 喂进 `VideoDecoder`，`requestAnimationFrame` 画到 canvas；键盘捕获时阻止浏览器快捷键；可选指针锁定。
- **离线**：Service Worker 预缓存应用壳（含全部路由分片），缩略图运行时缓存优先；上次确认的身份存本地，断网冷启动直接进应用，会话列表与消息读 IndexedDB。发出的消息先写 IndexedDB 发件箱再显示，连上后按入队顺序补发（服务器按 clientMessageId 幂等），≤ 50 MB 的附件原文件一并保存以便刷新后续传；连接断开时先探测 `/api/auth/me` 区分"离线 / 服务器不可达 / 登录态丢失"再决定重试还是重新验证。
- **会话过期**：Cloudflare Access 的 Cookie 到期后，请求会被 302 到 Cloudflare 登录页。API 客户端不跟随重定向，看到 opaqueredirect 即判定过期，整页导航到 `/api/auth/reauth?returnUrl=…`：页面本身由 Service Worker 从缓存返回、整页刷新到不了 Cloudflare 边缘，只有 `/api` 路径绕过 Service Worker，Access 才有机会拦下去登录，登录后跳回原页面。
- **更新**：Service Worker 用 prompt 模式，新版本下载后提示"立即更新"，不在录音、上传途中自动刷新。
- **退出 / 换人**：退出登录时退订本机推送并清空 IndexedDB、缩略图缓存与续传指纹；检测到另一个账号登录同一浏览器时同样先清空。

### 5.4 信息架构

- 桌面（≥1024 px）三栏：左侧 72 px 图标栏（消息 / 通话 / 网盘 / 时光 / 设备 / 设置），中栏 320 px 列表，右栏内容。
- 手机：底部 5 个 Tab：消息、通话、网盘、时光、我的。设备与远程控制放在"我的"里。
- 长辈模式：底部只剩 消息 / 通话 / 时光 三项。

### 5.5 线框图

**消息（桌面）**

```
┌────┬────────────────────┬───────────────────────────────────────────────┐
│ 🏠 │ 🔍 搜索消息 / 文件  │  全家群 · 6 人                   📞  🎥  👥  ⋯ │
│    ├────────────────────┼───────────────────────────────────────────────┤
│ 💬 │ ● 全家群     12:30 │                ── 今天 ──                     │
│ 📞 │   妈：晚上吃饺子    │ (妈) 晚上回来吃饺子🥟                   12:28 │
│ 📁 │ ○ 爸         昨天  │ (爸) 好  👍2                            12:29 │
│ 🖼 │   [图片]           │                     收到，6 点到 ✓✓ 12:30 (我) │
│ 💻 │ ○ 妹妹       周一  │ (妹妹) ┌──────────┐                           │
│    │   [语音 0:12]      │        │  缩略图   │ IMG_2031.heic             │
│    │ ○ 客厅(会议室) 常驻 │        └──────────┘                           │
│    │                    │ (系统) 爸 发起了视频通话 · 12:03 · 未接听       │
│ ⚙️ │                    ├───────────────────────────────────────────────┤
│ 👤 │ ＋ 新建群聊         │ 😊  📎  🎤  │ 输入消息…                │ 发送 │
└────┴────────────────────┴───────────────────────────────────────────────┘
```

**通话 / 会议**

```
┌───────────────────────────────────────────────────────────────┐
│ 客厅 · 4 人 · 00:12:35                              ⛶  💬  ⋯  │
│ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐          │
│ │     爸        │ │     妈 🔇     │ │  妹妹（屏幕）  │          │
│ │  [视频画面]    │ │  [视频画面]    │ │  [共享画面]    │          │
│ └───────────────┘ └───────────────┘ └───────────────┘          │
│                    ┌───────────────┐                           │
│                    │  我（预览）    │                           │
│                    └───────────────┘                           │
│      🎤 静音   📷 摄像头   🖥 共享屏幕   💬 聊天   📞 挂断         │
└───────────────────────────────────────────────────────────────┘
```

**网盘**

```
┌────┬────────────────┬──────────────────────────────────────────────┐
│    │ 我的文件        │ 家庭共享 / 2026 / 国庆旅行        ⬆ 上传  ＋ 新建 │
│    │ 家庭共享  ●     │ ──────────────────────────────── ▦ 网格  ☰ 列表 │
│    │ 相册（时间线）  │ 📁 照片       📁 视频       📄 行程.pdf  3.2 MB │
│    │ 与我共享        │ 🖼 IMG_1.jpg  🖼 IMG_2.jpg  🎞 VID_1.mp4 1.1 GB │
│    │ 回收站          │                                              │
│    │                │ 右键 / 长按：下载 · 分享给… · 发送到聊天 · 移动 · 删除 │
│    │ 已用 412 GB/2 TB│ ──────────────────────────────────────────── │
│    │ ▓▓▓▓▓░░░░░      │ 上传队列：VID_2.mp4 46% ⏸ （断网自动续传）      │
└────┴────────────────┴──────────────────────────────────────────────┘
```

**时光（动态）**

```
┌───────────────────────────────────────┐
│ 时光                        📷 发动态 │
│ ┌───────────────────────────────────┐ │
│ │ 🕰 那年今天 · 2019 · 外婆生日 [3 张]│ │
│ └───────────────────────────────────┘ │
│ ┌───────────────────────────────────┐ │
│ │ (妈) 2 小时前                      │ │
│ │ 饺子包好了！                       │ │
│ │ [图][图][图]                        │ │
│ │ ❤️ 爸、妹妹    💬 2                │ │
│ │   爸：我六点到                     │ │
│ └───────────────────────────────────┘ │
│ 相册 ▸ 2026-10 国庆旅行 · 2026-09 …   │
└───────────────────────────────────────┘
```

**设备 / 远程控制**

```
┌────────────────────────────────────────────────────────────┐
│ 设备                                                       │
│ ● 爸的电脑（Windows 11）· 在线 · 1920×1080 · 2 块显示器      │
│      [👁 查看] [🖱 远程控制] [⚙ 策略：需同意]                 │
│ ○ 妈的笔记本（Windows 10）· 离线 · 3 天前                    │
│ ● 我的 iPhone（浏览器）· 本机                                 │
├────────────────────────────────────────────────────────────┤
│ 远程控制 · 爸的电脑 · 1080p / 20 fps · 2.1 Mbps · 延迟 85 ms │
│ ┌────────────────────────────────────────────────────────┐ │
│ │                                                        │ │
│ │                 [远端桌面画面 canvas]                    │ │
│ │                                                        │ │
│ └────────────────────────────────────────────────────────┘ │
│ 显示器 [1][2]  画质 [流畅|清晰]  ⌨ Ctrl+Alt+Del  📋 剪贴板  ✖ 断开 │
└────────────────────────────────────────────────────────────┘
```

**手机 · 长辈模式**

```
┌──────────────┐
│  家           │
│ ┌──────────┐ │
│ │ ● 全家群  │ │
│ │ 妈：晚上… │ │
│ └──────────┘ │
│ ┌──────────┐ │
│ │ ○ 儿子    │ │
│ └──────────┘ │
│              │
│ [ 📞 打给儿子 ]│  ← 一键常用联系人，按钮 ≥56 px
│              │
├──────────────┤
│ 消息 通话 时光 │
└──────────────┘
```

### 5.6 视觉与交互原则

- 温暖低饱和的家庭感配色：主色暖橙、底色奶油白；暗色模式用深棕灰。圆角 12 px，大头像。
- 长辈模式：字号 +25%，隐藏网盘与设备，按钮 ≥56 px，来电全屏大按钮，常用联系人一键拨打。
- 触控目标 ≥44 px；全部交互可键盘操作；中文优先。
- 空状态有引导（"邀请家人"）；错误可重试，不向用户暴露技术细节。

### 5.7 PWA 与移动端

- manifest：standalone、主题色、图标；Service Worker 缓存应用壳与静态资源。
- Web Push 首次使用时引导开启；iOS 需先添加到主屏幕。
- Share Target：安卓系统分享照片可直接进入"发给谁"。
- 后期 Capacitor 套壳（同一套 Vue 代码）获得原生推送、CallKit 后台来电、相册自动备份。

---

## 6. 部署

### 6.1 容器清单

| 服务 | 镜像 | 必需 | 说明 |
|---|---|---|---|
| cloudflared | `cloudflare/cloudflared` | 是 | 出站隧道，把 `chat.example.com` 指到 `http://web:80`，服务器不开入站端口 |
| web | 本地构建（node 构建 → `nginx:alpine`） | 是 | 托管 Vue 静态文件；反代 `/api` `/hubs` `/ws` `/s` 到 api |
| api | 本地构建（.NET 10，含 ffmpeg） | 是 | 纯 API，不托管前端 |
| postgres | `postgres:17-alpine` | 是 | |
| hbbs / hbbr | `rustdesk/rustdesk-server` | 否（profile `rustdesk`） | 远程控制兜底，需要自己开端口 |

通话媒体由 Cloudflare Realtime 承担，家里没有媒体服务器。草案文件在 [deploy/](../deploy/)：`docker-compose.yml`、`nginx.conf`、`Dockerfile.api`、`Dockerfile.web`、`.env.example`、`init-env.sh` / `init-env.ps1`。

### 6.2 网络：全部出站

| 流量 | 路径 | 说明 |
|---|---|---|
| 网页、REST、SignalR、远程控制中继、分享链接 | 浏览器 → Cloudflare 边缘 → 隧道 → nginx | 全部 HTTPS / WSS |
| 通话媒体 | 浏览器 ⇄ Cloudflare Realtime SFU（任播），严格网络下经 Cloudflare TURN | 不进家庭网络 |
| 通话编排 | api → Cloudflare Realtime REST（出站 HTTPS） | 会话 / 轨道 / TURN 凭据 |
| Web Push | api → 推送服务（出站） | |

端口一览：

| 端口 | 协议 | 服务 | 路由器开放 | 说明 |
|---|---|---|---|---|
| 无 | — | cloudflared / web / api / postgres | 否 | 全部出站 |
| 8080 | TCP | web（nginx） | 否，仅局域网 | 可选：断外网时局域网直连 |
| 21115–21119 | TCP / UDP | rustdesk | profile `rustdesk` 需要 | |

Cloudflare 的几个硬限制：单个请求体 100 MB（免费 / Pro 套餐），所以上传用 8 MB 分片；代理空闲连接约 100 s 超时，所以 WebSocket 要有心跳；WebSocket 流量与下载响应不限大小；Realtime 媒体出站每月前 1 TB 免费。

### 6.3 Cloudflare 配置（一次性，约 30 分钟）

1. **隧道**：Zero Trust → Networks → Tunnels → 创建 Cloudflared 隧道，拿到 token 填入 `.env` 的 `TUNNEL_TOKEN`。
2. **公共主机名**：在隧道里添加 `chat.example.com` → 服务 `http://web:80`。cloudflared 与 web 在同一个 compose 网络里，直接用服务名。
3. **Access 应用**：Access → Applications → Self-hosted，域名 `chat.example.com`，会话时长 30 天。
   - 策略「家人」：Allow，Include = Emails（列出家人邮箱）；登录方式启用 One-time PIN，有条件再加 Google / GitHub。
   - 策略「Agent」：Service Auth，Include = 下一步创建的服务令牌。
   - （可选）另建应用 `chat.example.com/s`，Bypass，Include = Everyone，用于对外分享链接。
4. **服务令牌**：Access → Service Auth → 创建，Client ID / Secret 在安装 Agent 的设备上填入。
5. **AUD**：复制 Access 应用的 Application Audience（AUD）Tag 与团队名，填入 `.env` 的 `CF_ACCESS_AUD`、`CF_ACCESS_TEAM`，api 用它们校验 JWT。
6. **Realtime SFU**：Cloudflare 控制台 → Realtime → SFU → 创建应用，拿到 App ID 与 App Secret，填入 `CF_REALTIME_APP_ID`、`CF_REALTIME_APP_SECRET`。
7. **Realtime TURN**：Realtime → TURN → 创建密钥，拿到 Key ID 与 API Token，填入 `CF_TURN_KEY_ID`、`CF_TURN_KEY_API_TOKEN`；api 用它为每次通话签发短期凭据。

### 6.4 Dockerfile

前端 `deploy/Dockerfile.web`：

```dockerfile
FROM node:24-alpine AS build
WORKDIR /src
COPY web/package*.json ./
RUN npm ci
COPY web/ .
RUN npm run build

FROM nginx:1.27-alpine
COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist /usr/share/nginx/html
```

后端 `deploy/Dockerfile.api`：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/PrivateChat.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PrivateChat.Api.dll"]
```

### 6.5 一键部署

```bash
git clone <仓库> && cd PrivateChat/deploy
./init-env.sh            # 交互输入域名、管理员邮箱、隧道 token、Access 团队名与 AUD、Realtime 的 App 与 TURN 密钥，自动生成数据库密码
docker compose up -d     # 首次会构建 web 与 api 镜像
```

打开 `https://chat.example.com` → Cloudflare 邮箱验证码登录 → `ADMIN_EMAIL` 对应的账号自动成为管理员 → 在"家庭"页填入家人邮箱（同时也要加进 Access 策略）。

### 6.6 备份与升级

- 备份：`pg_dump` 每日 + `data` 卷用 restic 增量备份到另一块盘 / 云存储；提供 `scripts/backup.sh`。
- 升级：`git pull && docker compose build && docker compose up -d`；数据库迁移在 api 启动时自动执行。
- 回滚：镜像打 tag；EF 迁移保持向后兼容（先加列后删列）。
- cloudflared、postgres 镜像随 `docker compose pull` 更新。

### 6.7 资源估算

| 项 | 估算 |
|---|---|
| 内存 | api 300–500 MB（转码峰值 1 GB）、postgres 256 MB、nginx + cloudflared 50 MB → 2 GB 起步，4 GB 从容 |
| CPU | 4 核；视频转码是主要消耗 |
| 磁盘 | 系统盘 SSD；数据盘按照片视频量 |
| 带宽 | 通话媒体不经过家里，服务器上行只剩文件下载与远程控制中继（单路 2–8 Mbps） |
| Cloudflare 用量 | Realtime 媒体出站每月前 1 TB 免费；6 人 720p 会议约 13 GB / 小时，相当于每月 70 多小时 |

---

## 7. 实施路线图

| 里程碑 | 业余投入 | 交付 |
|---|---|---|
| M0 骨架 | 1 周 | 仓库结构、compose、Cloudflare 隧道与 Access 接入、邮箱自动建号 / 邀请、空壳前端、CI 构建镜像 |
| M1 聊天 | 3 周 | 文字 / 图片 / 语音 / 视频 / 文件消息、群、已读、撤回、表情回应、推送、PWA |
| M2 网盘 | 2 周 | tus 上传、目录、共享、相册时间线、发送到聊天、回收站 |
| M3 通话 | 3 周 | Cloudflare Realtime 会话 / 轨道编排、1:1 / 会议、屏幕共享、客厅、通话记录、来电推送 |
| M4 时光 | 2 周 | 动态 / 评论 / 点赞 / 那年今天 / 相册 |
| M5 远程 | 3–4 周 | Agent v1（Windows）+ 中继 + 浏览器控制端 + 审计 |
| M6 打磨 | 持续 | 长辈模式、搜索、WebDAV、备份脚本、Capacitor |

一个人业余做，M0–M5 约 4–5 个月。建议 M1 完成就让家人用起来，后面按反馈排序。

---

## 8. 风险与待决问题

**风险**

| 风险 | 对策 |
|---|---|
| 家庭宽带无公网入站 | 全部流量走隧道和 Cloudflare Realtime，不需要任何入站端口 |
| iOS 浏览器限制：后台来电不可靠、不能共享屏幕 | 来电靠推送 + 铃声页面；后期 Capacitor |
| iPhone HEIC / HEVC 兼容 | 服务端统一转码，原件保留 |
| 远程控制 UAC / 锁屏 | v1 明确不支持，v2 用 SYSTEM 服务 |
| 转码吃 CPU | 并发度 2、夜间低优先级、可选跳过 1080p 以下 |
| 单点故障 | 每日备份；家庭场景可接受停机 |
| 家人在中国大陆访问 Cloudflare 节点延迟高、不稳定 | 实测为准；通话质量同样取决于到 Cloudflare 的链路 |
| Access 会话过期打断使用 | 会话设 30 天；前端检测到重定向后自动引导重新验证 |
| 依赖 Cloudflare 可用性 | 局域网 8080 直连 + 密码登录作备用（此时无通话） |
| Cloudflare Realtime 产品较新、API 可能演进 | 所有调用收在 CloudflareRealtimeClient 一处；管理页监控用量别超 1 TB |

**已确认**：入口走 Cloudflare Zero Trust（隧道 + Access）；通话媒体用 Cloudflare Realtime SFU + TURN；数据库保持 PostgreSQL；API 用 Controller 风格；UI 库用 Element Plus；被控电脑全部是 Windows，Agent 只做 Windows。

**需要你拍板的问题**

1. 成员规模与存储量级（几百 GB 还是几 TB）？决定是否一开始就接 S3。
2. 是否需要原生 App（后台来电、相册自动备份）？决定何时上 Capacitor。

---

## 附录 A · REST API 草案

```
Auth      GET  /api/auth/me（凭 Cf-Access-Jwt-Assertion 自动登录 / 建号） ; POST /api/auth/local/login（局域网备用）
          GET  /api/auth/reauth?returnUrl=（Access 过期后的重新验证跳板，只接受站内地址）
          POST /api/auth/device/code · /device/poll · /device/confirm      设备码登录
Family    GET/PUT /api/family ; GET /api/family/members ; PATCH /api/family/members/{id}
          POST /api/family/invites ; POST /api/family/join
Chat      GET/POST /api/conversations ; GET /api/conversations/{id}/messages?beforeSeq=&limit=
          PUT/DELETE /api/messages/{id} ; POST /api/messages/{id}/reactions
          POST /api/conversations/{id}/read                                  （发送走 SignalR）
Blobs     POST /api/blobs/precheck ; /api/uploads（tus） ; GET /api/blobs/{id}?v= ; GET /s/{token}
Drive     GET /api/drive（空间与用量） ; PATCH /api/drive/spaces/{id}（管理员：配额、孩子只读）
          GET /api/drive/list?spaceId=&folderId= ; GET /api/drive/spaces/{id}/folders（移动对话框）
          POST /api/drive/folders ; POST /api/drive/files（上传 / 秒传后登记，重名 Rename / Replace）
          PATCH /api/drive/nodes/{id}（重命名 / 移动，可跨空间） ; DELETE /api/drive/nodes/{id}（进回收站）
          GET /api/drive/trash ; POST /api/drive/trash/{id}/restore ; DELETE /api/drive/trash/{id} ; DELETE /api/drive/trash
          GET /api/drive/shared-with-me ; GET/POST /api/drive/nodes/{id}/shares ; DELETE /api/drive/shares/{id}
          POST /api/drive/nodes/{id}/send（发送到聊天） ; GET /api/drive/gallery?before=&beforeId=&limit=
Share     GET /s/{token}[?folder=] ; POST /s/{token}（密码） ; GET /s/{token}/f/{nodeId}?v=&download=
Calls     POST /api/calls ; POST /api/calls/{id}/join（建 Cloudflare 会话 + 返回 TURN 凭据）
          POST /api/calls/{id}/tracks（发布，SDP offer → answer） ; POST /api/calls/{id}/tracks/pull（订阅）
          POST /api/calls/{id}/renegotiate ; DELETE /api/calls/{id}/tracks/{name}
          POST /api/calls/{id}/leave · /end ; GET /api/calls?conversationId= ; GET /api/rooms
Moments   GET /api/posts?before= ; POST/DELETE /api/posts ; POST /api/posts/{id}/comments
          POST /api/posts/{id}/reactions ; GET /api/posts/on-this-day ; GET/POST /api/albums
Remote    GET /api/devices ; PATCH /api/devices/{id}（策略 / 名称） ; DELETE /api/devices/{id}（吊销）
          POST /api/remote/sessions ; POST /api/remote/sessions/{id}/consent · /end
          GET /api/remote/sessions（审计） ; WS /ws/remote/{sessionId}/{agent|controller}
Notify    GET /api/notifications ; POST /api/notifications/read ; POST/DELETE /api/push/subscribe
Search    GET /api/search?q=&type=
Admin     GET /api/admin/stats ; GET /api/admin/jobs ; GET /healthz ; GET /openapi/v1.json
```

## 附录 B · SignalR 事件

| 方向 | 名称 | 载荷 |
|---|---|---|
| C→S | SendMessage | conversationId, clientMessageId, type, body, attachments[], replyToId |
| C→S | Typing | conversationId |
| C→S | MarkRead | conversationId, seq |
| C→S | Sync | { conversationId: lastSeq } |
| S→C | MessageReceived / MessageUpdated / MessageDeleted | MessageDto |
| S→C | ReactionChanged | messageId, reactions[] |
| S→C | ReadReceipt | conversationId, userId, seq |
| S→C | Typing | conversationId, userId |
| S→C | Presence | userId, online, lastSeenAt |
| S→C | ConversationUpdated | ConversationDto |
| S→C | IncomingCall / CallUpdated | CallDto |
| S→C | ParticipantJoined / ParticipantLeft | callId, participant |
| S→C | TrackPublished / TrackUnpublished | callId, participantId, cfSessionId, trackName, kind |
| S→C | DriveChanged | spaceId, parentId |
| S→C | Notification | NotificationDto |
| S→C | RemoteSessionChanged | RemoteSessionDto |

## 附录 C · 远程控制数据面帧格式

二进制帧，第 1 字节为类型，其余为载荷；数值小端。

| 类型 | 方向 | 载荷 |
|---|---|---|
| 0x01 VideoConfig | A→C | codec(u8), width(u16), height(u16), fps(u8) |
| 0x02 VideoFrame | A→C | timestampMs(u32), isKey(u8), nal bytes |
| 0x03 Cursor | A→C | x(f32), y(f32), shapeId(u8) |
| 0x04 Monitors | A→C | count(u8), [index, width, height, isPrimary]… |
| 0x05 Clipboard | 双向 | utf8 text |
| 0x06 Stats | A→C | fps(u8), kbps(u16), cpu(u8) |
| 0x10 MouseMove | C→A | x(f32 0..1), y(f32 0..1) |
| 0x11 MouseButton | C→A | button(u8), down(u8) |
| 0x12 Wheel | C→A | dx(i16), dy(i16) |
| 0x13 Key | C→A | scanCode(u16), down(u8), modifiers(u8) |
| 0x14 SelectMonitor | C→A | index(u8) |
| 0x15 SetQuality | C→A | preset(u8：0 流畅 / 1 清晰) |
| 0x16 RequestKeyFrame | C→A | 无 |
| 0x17 SpecialKey | C→A | id(u8：Ctrl+Alt+Del 等) |
