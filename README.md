# PrivateChat

家庭私有通讯平台：聊天（文字 / 图片 / 语音 / 视频 / 文件）、音视频通话与会议、网盘与共享、家庭动态、远程控制（被控端 Windows）。

- 后端：.NET 11 · ASP.NET Core（Controller）· SignalR · PostgreSQL
- 通话：Cloudflare Realtime SFU + TURN，媒体不经过家庭网络
- 前端：Vue 3 · TypeScript · Vite · Element Plus · PWA，由 nginx 托管
- 入口：Cloudflare Tunnel + Cloudflare Access（零入站端口、无需证书）
- 部署：Docker Compose 一键

当前状态：M0 骨架、M1 聊天（含 PWA 离线与推送）、M2 网盘、M3 通话（1:1 音视频、客厅会议室、屏幕共享、来电推送、通话记录）已完成；M4 动态、M5 远程控制待做。设计稿见 [docs/DESIGN.md](docs/DESIGN.md)，部署见 [deploy/](deploy/)。

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
cd web && npm run test:e2e-cli                 # 通话 CLI 模式与配置检查（无需 Docker / 网络）
cd web && npx playwright install chromium && npm run e2e   # 真实 Chromium 端到端（需先启动 compose 栈）
```

通话的端到端测试（`web/e2e/calls.cjs`）默认不需要 Cloudflare 凭据：它会启动一个本机的假 SFU（`web/e2e/fake-sfu.cjs`，一个 headless Chromium 页面真实转发媒体），临时把 api 容器指过去，结束后按 `deploy/.env` 还原。

真实 Cloudflare 模式：在 `deploy/.env` 填好 `CF_REALTIME_APP_ID`、`CF_REALTIME_APP_SECRET`、`CF_TURN_KEY_ID`、`CF_TURN_KEY_API_TOKEN`，在项目根目录执行 `docker compose -f deploy/docker-compose.yml up -d api` 使配置生效，然后在 PowerShell 运行：

```powershell
$env:E2E_REAL_CF = '1'
try { node web/e2e/calls.cjs } finally { Remove-Item Env:E2E_REAL_CF }
```

真实模式读取运行中 api 容器的配置，不启动假 SFU、不重建 api；缺少凭据或仍指向假 SFU 时，在修改测试数据与启动浏览器前退出。它使用合成摄像头、麦克风和共享画布，媒体经过真实 Cloudflare，会产生用量。七项依赖假 SFU 请求记录或主动断线的断言会显示为 `SKIP`，最终分别统计通过、失败与跳过。

两种模式都只用于本地 compose **测试库**（`http://localhost:8080`），会创建 admin / bob 之间的通话记录并在需要时创建 bob 测试账号。已有通话或在场参与者时会退出，不强制清空通话状态。真实模式不替代手机真机、后台来电和 Wi-Fi / 蜂窝网络切换验证。

### 测试栈放在服务器上

不想在写代码的电脑上起 compose 栈和数据库时，用 `web/e2e/test-host.cjs` 把测试栈放到一台 Linux 服务器上（可以就是跑线上那套的机器）：它把工作区的代码（含没提交的改动）同步过去，在那边构建并启动一套隔离的栈——项目名 `privatechat-test`，自己的镜像标签、数据卷和 `test.env`，端口只绑服务器的本机回环，不接隧道——线上那套不受影响。写代码的电脑上只剩一条 SSH 隧道、测试用的浏览器和临时启动的被控端程序（它是 Windows 程序，服务器上跑不了）。

连哪台服务器写在 `web/e2e/.test-host.json` 里（不进仓库，格式见脚本开头的说明）。在 `web/` 下：

```bash
node e2e/test-host.cjs up            # 同步代码、构建、启动；改了代码之后再跑一次就是更新
node e2e/test-host.cjs dotnet-test   # .NET 测试在服务器上的临时容器里跑，数据库用测试栈的
node e2e/test-host.cjs tunnel        # 开隧道（另开一个终端，开着）
eval "$(node e2e/test-host.cjs env)" # E2E_BASE / E2E_COMPOSE / E2E_SFU_PORT
node e2e/remote.cjs --realtime       # 之后各个 e2e 脚本照常跑，对着的就是服务器上的测试栈
node e2e/test-host.cjs down          # 用完停掉（加 -v 连测试数据一起删）
```
