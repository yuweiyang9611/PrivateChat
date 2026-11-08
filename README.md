# PrivateChat

家庭私有通讯平台：聊天（文字 / 图片 / 语音 / 视频 / 文件）、音视频通话与会议、网盘与共享、家庭动态、远程控制（被控端 Windows）。

- 后端：.NET 10 · ASP.NET Core（Controller）· SignalR · PostgreSQL
- 通话：Cloudflare Realtime SFU + TURN，媒体不经过家庭网络
- 前端：Vue 3 · TypeScript · Vite · Element Plus · PWA，由 nginx 托管
- 入口：Cloudflare Tunnel + Cloudflare Access（零入站端口、无需证书）
- 部署：Docker Compose 一键

当前状态：M0 骨架已搭好（后端可编译、单元测试通过、初始迁移已生成；前端可构建）。设计稿见 [docs/DESIGN.md](docs/DESIGN.md)，部署草案见 [deploy/](deploy/)。

## 本地开发

```bash
docker run -d --name pc-postgres -e POSTGRES_DB=privatechat -e POSTGRES_USER=privatechat -e POSTGRES_PASSWORD=privatechat -p 5432:5432 postgres:17-alpine
```

```bash
dotnet run --project src/PrivateChat.Api
```

```bash
cd web && npm install && npm run dev
```

后端默认 http://localhost:8080（OpenAPI 在 /openapi/v1.json，Scalar 在 /scalar），前端 dev 服务器把 /api、/hubs 代理过去。本地无 Cloudflare Access 头时用 POST /api/auth/local/login 密码登录。

## 测试

```bash
dotnet test tests/PrivateChat.Api.Tests      # 单元 + 集成测试；集成测试连开发用的 pc-postgres（或 PC_TEST_PG 指定的库），连不上会跳过
cd web && npm test                            # 前端单元测试（vitest）
cd web && npx playwright install chromium && npm run e2e   # 真实 Chromium 端到端（需先启动 compose 栈）
```
