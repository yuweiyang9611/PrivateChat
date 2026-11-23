// 远程控制端到端：真实 Edge（WebCodecs 解 H.264）对 compose 栈，加一个本机无界面 Agent。
// Agent 用测试画面（四色象限 + 移动黑块，两块假显示器 1280×720 / 1920×1080），键鼠不注入系统、写到日志文件——
// 不碰这台电脑的真实屏幕和鼠标键盘。E2E_REAL_SCREEN=1 时最后再用真实屏幕跑一遍（只检查尺寸和非黑屏，不截图）。
// 覆盖：下载安装包、设备码添加（管理员替爸爸添加）、主人实时看到在线 / 被远程提醒、远程控制（自动同意）、
// 画面像素、声音（测试音）、键鼠 / 滚轮 / 文字 / 特殊键 / 剪贴板、画质菜单（自动 / 清晰 / 原画 / 自定义，记在这台设备上）与切换显示器、
// 结束与记录、诊断（管理员看这台电脑的硬件和编码器、刚才这次远程两边报上来的汇总；主人看不到）、
// 只看模式不注入（画质照样能换）、对方电脑忙不过来时自动画质降分辨率并提示（编码故意放慢：降了管用，留着；
// 截屏故意放慢：降了也没快多少，退回去，不再降）、拒绝、
// 免同意名单（勾上后不经同意直接连、移出后立即结束）、同意窗口没人点时取消 / 改用 PIN（窗口马上收起、PIN 立刻连上）、移除电脑。
// 加 --agent-tls13：被控端不直连 compose 栈，而是经本机一个只收 TLS 1.3 的入口（e2e/tls13-proxy.cjs），
// 并强制用程序自带的 TLS 1.3（Windows 10 上的做法）——同一套用例再跑一遍，另外断言三条连接都是 TLS 1.3。
// 加 --realtime：快速线路——画面和声音经 Cloudflare Realtime（这里是本机的假 SFU，e2e/fake-sfu.cjs，用 Edge 起），不经我们的中继。
// 脚本先把 api 指到假 SFU 上重建；网页默认选标准线路（原有用例照旧在中继上跑），中间插一段专门测快速线路：
// 切过去之后画面 / 声音 / 键鼠、中继上不再有画面帧、故意丢包靠重发补上、掐断后不换线路而是自己重新接上、接不上时提示并手动切到标准线路。
// 不加 --realtime 时被控端用 PRIVATECHAT_AGENT_REALTIME=off 关掉快速线路：api 配着真的 Cloudflare 也不会去连。
// 加 --encoder hardware|software|auto：被控端用 PRIVATECHAT_AGENT_ENCODER=…（hardware = 没探测验证过也用显卡的硬件编码器），
// 同一套用例照样要过；hardware 时另外断言"编码 …"那一行写的是一个（硬件）编码器、中途没有换回软件编码器。
// 截屏方式跟着环境变量 PRIVATECHAT_AGENT_CAPTURE 走（测试画面用不着；E2E_REAL_SCREEN=1 那一段会说用的是哪种，
// 设成 dxgi 时还断言用上了桌面复制——锁着屏的电脑上跑不了这一段）。
// 前提：compose 栈已启动，装有 Edge；用法：cd web && node e2e/remote.cjs [--agent-tls13] [--realtime] [--encoder hardware]
// 对着另一套 compose 栈跑：E2E_BASE（网页地址）、E2E_COMPOSE（docker compose 命令，带 -p / -f）
const fs = require('fs')
const os = require('os')
const path = require('path')
const { chromium } = require('playwright')
const { execSync, spawn } = require('child_process')

const BASE = process.env.E2E_BASE || 'http://localhost:8080'
const ROOT = path.resolve(__dirname, '../..')
const COMPOSE = process.env.E2E_COMPOSE || `docker compose -f "${path.resolve(ROOT, 'deploy/docker-compose.yml')}"`
const AGENT = path.resolve(ROOT, 'src/PrivateChat.Agent/bin/Debug/net11.0-windows/PrivateChat.Agent.exe')
const TLS13 = process.argv.includes('--agent-tls13')
const REALTIME = process.argv.includes('--realtime')
/** --encoder X：被控端的 PRIVATECHAT_AGENT_ENCODER（不给就不设，照环境变量 / 默认的 auto） */
const ENCODER = process.argv.includes('--encoder') ? process.argv[process.argv.indexOf('--encoder') + 1] : null
if (ENCODER !== null && !['auto', 'hardware', 'software'].includes(ENCODER)) {
  console.error(`--encoder 只能是 auto / hardware / software，现在是：${ENCODER}`)
  process.exit(2)
}
/** --realtime 时：假 SFU 监听的端口，和让 api 连它的环境变量（和 calls.cjs 用的是同一组） */
const SFU_PORT = Number(process.env.E2E_SFU_PORT || 8788)
const FAKE_SFU_ENV = {
  CF_REALTIME_BASE_URL: `http://host.docker.internal:${SFU_PORT}/v1`,
  CF_REALTIME_APP_ID: 'e2e-app',
  CF_REALTIME_APP_SECRET: 'e2e-secret',
  CF_TURN_KEY_ID: 'e2e-turn',
  CF_TURN_KEY_API_TOKEN: 'e2e-turn-token',
}
let sfu = null
/** --realtime 时中途会故意把假 SFU 停掉：记下是什么时候（看 api 日志时要分前后） */
let sfuStoppedAt = null
/** 这个脚本起的被控端进程：出错退出时只收掉它们（不按进程名全杀，别的测试、这台电脑上真在用的被控端不受影响） */
const agents = new Set()
/** --agent-tls13 时：本机的 TLS 1.3 入口（被控端连它，它转给 compose 栈） */
let tls = null
const results = []
function check(name, ok, detail) {
  results.push({ name, ok: !!ok })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail !== undefined ? '  — ' + JSON.stringify(detail) : ''}`)
}
const sql = (q) => execSync(`${COMPOSE} exec -T postgres psql -U privatechat -d privatechat -At -c "${q}"`).toString().trim()
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

async function until(fn, timeout = 15000, every = 200) {
  const deadline = Date.now() + timeout
  for (;;) {
    const v = await fn()
    if (v) return v
    if (Date.now() > deadline) return null
    await sleep(every)
  }
}

async function login(page, who) {
  await page.goto(BASE + '/')
  await page.locator('input[autocomplete=username]').fill(who)
  await page.locator('input[type=password]').fill('admin123')
  await page.getByRole('button', { name: '登录' }).click()
  await page.locator('input[type=password]').waitFor({ state: 'detached', timeout: 15000 })
}

/** --realtime：带着指向假 SFU 的那组环境变量重建 api 容器，等它能用 */
async function recreateApi() {
  execSync(`${COMPOSE} up -d --no-deps api`, { env: { ...process.env, ...FAKE_SFU_ENV }, stdio: 'ignore' })
  return apiHealthy()
}

/** 等 api 重建后能用（最多 90 秒） */
function apiHealthy() {
  return until(async () => (await fetch(BASE + '/healthz').catch(() => null))?.ok, 90000, 500)
}

/** 起一个无界面 Agent；返回进程、它的输出（累积）、键鼠日志路径。extraEnv：这一次额外带的环境变量（测试钩子） */
function startAgent(dir, consent, source = 'pattern', extraEnv = {}) {
  fs.mkdirSync(dir, { recursive: true })
  const inputLog = path.join(dir, 'input.jsonl')
  const args = ['--headless', '--server', tls ? tls.url : BASE, '--data-dir', dir, '--name', 'E2E 测试电脑', '--consent', consent]
  if (source === 'pattern') args.push('--source', 'pattern', '--dry-input', inputLog)
  else args.push('--dry-input', inputLog) // 真实屏幕也不注入真实键鼠
  const env = { ...process.env, PRIVATECHAT_AGENT_TEST: '1' }
  // 快速线路：只在 --realtime 时开；开的时候故意丢 2% 的画面包，看重发管不管用
  if (REALTIME) env.PRIVATECHAT_AGENT_TEST_RTP_DROP = '0.02'
  else env.PRIVATECHAT_AGENT_REALTIME = 'off'
  // 强制走程序自带的 TLS 1.3，只信任测试入口那张临时证书
  if (tls) Object.assign(env, { PRIVATECHAT_AGENT_TLS: 'bundled', PRIVATECHAT_AGENT_TEST_CA: tls.cert })
  if (ENCODER) env.PRIVATECHAT_AGENT_ENCODER = ENCODER
  Object.assign(env, extraEnv)
  const proc = spawn(AGENT, args, { env })
  agents.add(proc)
  proc.on('exit', () => agents.delete(proc))
  const agent = { proc, out: '', inputLog }
  proc.stdout.on('data', (d) => { agent.out += d.toString() })
  proc.stderr.on('data', (d) => { agent.out += d.toString() })
  return agent
}

function inputs(agent) {
  if (!fs.existsSync(agent.inputLog)) return []
  return fs.readFileSync(agent.inputLog, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l))
}

/** 画布上某个相对位置的像素（RGB） */
const pixel = (page, fx, fy) => page.evaluate(([fx, fy]) => {
  const c = document.querySelector('[data-testid=remote-canvas]')
  if (!c || !c.width) return null
  const d = c.getContext('2d').getImageData(Math.floor(c.width * fx), Math.floor(c.height * fy), 1, 1).data
  return [d[0], d[1], d[2]]
}, [fx, fy])
const near = (rgb, want, tol = 40) => !!rgb && rgb.every((v, i) => Math.abs(v - want[i]) <= tol)
const canvasSize = (page) => page.evaluate(() => {
  const c = document.querySelector('[data-testid=remote-canvas]')
  return c ? `${c.width}x${c.height}` : ''
})
/** 工具栏上显示的解码帧率 */
const shownFps = async (page) => Number((await page.locator('[data-testid=remote-stats]').textContent().catch(() => '')).match(/(\d+) fps/)?.[1] ?? 0)
/** 被控端输出里从 from 起的"编码 …"那几行（检查失败时打出来看） */
const encodeLines = (agent, from = 0) => agent.out.slice(from).match(/编码 \d+x\d+[^\r\n]*/g) ?? []
/** "编码 …"那一行最后写的编码器和截屏方式（"…，编码器 X，截屏 Y"；老版本被控端没有这一截，都是 null） */
const pipelineOf = (line) => ({
  encoder: line?.match(/，编码器 (.+?)，截屏 /)?.[1] ?? null,
  capture: line?.match(/，截屏 (.+)$/)?.[1]?.trim() ?? null,
})
/** 被控端输出里从 from 起自动画质说的那几行 */
const autoLines = (agent, from = 0) => agent.out.slice(from).match(/自动画质：[^\r\n]*/g) ?? []

/** 画质菜单里选一项：auto / smooth / clear / original / custom */
async function pickVideo(page, key) {
  await page.locator('[data-testid=remote-video]').click()
  await page.locator(`[data-testid=remote-video-${key}]`).click()
}

/** 自定义那一行的下拉框里选一项（按显示的文字整串比较："5 帧"不能选成"15 帧"） */
async function pickOption(page, testid, label) {
  await page.locator(`[data-testid=${testid}]`).click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: new RegExp(`^\\s*${label}\\s*$`) }).click()
}

/** 鼠标移到画布上的相对位置（考虑 object-fit: contain 的黑边） */
async function moveOnCanvas(page, fx, fy) {
  const box = await page.locator('[data-testid=remote-canvas]').boundingBox()
  const size = await page.evaluate(() => { const c = document.querySelector('[data-testid=remote-canvas]'); return [c.width, c.height] })
  const scale = Math.min(box.width / size[0], box.height / size[1])
  const left = box.x + (box.width - size[0] * scale) / 2
  const top = box.y + (box.height - size[1] * scale) / 2
  await page.mouse.move(left + size[0] * scale * fx, top + size[1] * scale * fy)
}

;(async () => {
  const startedAt = new Date().toISOString()
  const stamp = Date.now()
  const dir = path.join(os.tmpdir(), `pc-agent-e2e-${stamp}`)
  let agent = null
  const cleanup = () => {
    try { agent?.proc.kill() } catch { /* 已退出 */ }
    try { tls?.close() } catch { /* 已关 */ }
    try { void sfu?.close() } catch { /* 已关 */ }
    // 数据目录里有（已吊销的）设备令牌：测完删掉
    for (const d of [dir, dir + '-real']) try { fs.rmSync(d, { recursive: true, force: true }) } catch { /* 进程还占着 */ }
  }
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK']) process.on(signal, () => { cleanup(); process.exit(1) })
  // 测完把 api 还原成原来的配置（按 .env），并等它重新起来：npm run e2e 里紧接着的下一套测试一上来就要连它，
  // 没等的话被控端第一次申请设备码正好撞上 api 重启（经 --agent-tls13 的入口时表现成 TLS 连接写不进去），那套测试就在第一步失败
  const restoreApi = async () => {
    if (!REALTIME) return
    execSync(`${COMPOSE} up -d --no-deps api`, { stdio: 'ignore' })
    if (!(await apiHealthy())) console.log('api 还原配置后 90 秒还没起来')
  }

  execSync(`dotnet build "${path.resolve(ROOT, 'src/PrivateChat.Agent')}" -nologo -v q`, { stdio: 'inherit' })
  sql("insert into users (id, email, username, display_name, password_hash, role, elder_mode, created_at) select '01a1b1b0-0000-7000-8000-0000000000b0', 'bob@example.com', 'bob', '爸爸', password_hash, 'Member', false, now() from users where email='admin@example.com' on conflict (id) do nothing")
  // 上一次中途失败没走到"移除电脑"那一步：留下的同名电脑先作废，不然设备列表里会有两台"E2E 测试电脑"
  sql("update devices set revoked_at = now(), token_hash = null where kind = 'Agent' and name = 'E2E 测试电脑' and revoked_at is null")

  // 安装包随 web 镜像发布
  const head = await fetch(BASE + '/downloads/PrivateChat.Agent.exe', { method: 'HEAD' })
  check('web 镜像里带着远程协助程序的安装包', head.ok && Number(head.headers.get('content-length')) > 10e6 && /attachment/.test(head.headers.get('content-disposition') ?? ''),
    { status: head.status, size: head.headers.get('content-length') })

  if (TLS13) tls = await require('./tls13-proxy.cjs').start(BASE)
  if (REALTIME) {
    sfu = await require('./fake-sfu.cjs').startFakeSfu({ port: SFU_PORT, channel: 'msedge' })
    check('api 改连本机的假 SFU（当作 Cloudflare Realtime），能用', !!(await recreateApi()), FAKE_SFU_ENV.CF_REALTIME_BASE_URL)
  }
  agent = startAgent(dir, 'accept')
  const code = (await until(() => agent.out.match(/DEVICE-CODE: (\d{8})/)?.[1]))
  check('Agent 第一次运行申请到 8 位设备码', !!code, agent.out.slice(-300))

  const browser = await chromium.launch({ channel: 'msedge' })
  const opts = { viewport: { width: 1280, height: 900 }, locale: 'zh-CN', timezoneId: 'Asia/Shanghai' }
  const adminCtx = await browser.newContext(opts)
  const bobCtx = await browser.newContext(opts)
  // 网页默认走快速线路；这里先都选成标准线路，原有用例在中继上跑，快速线路那一段自己点过去
  const standardLine = (ctx) => ctx.addInitScript(() => {
    try { if (!localStorage.getItem('remote.transport')) localStorage.setItem('remote.transport', 'relay') } catch { /* about:blank 上没有 localStorage */ }
  })
  await standardLine(adminCtx)
  await standardLine(bobCtx)
  const admin = await adminCtx.newPage()
  // 管理员这个页面的中继连接上收到了多少画面帧（0x02）、声音帧（0x08）：用来确认切到快速线路之后它们不再经中继
  const relayed = { video: 0, audio: 0 }
  admin.on('websocket', (ws) => {
    if (!ws.url().endsWith('/controller')) return
    ws.on('framereceived', ({ payload }) => {
      if (!Buffer.isBuffer(payload)) return
      if (payload[0] === 0x02) relayed.video++
      if (payload[0] === 0x08) relayed.audio++
    })
  })
  const bob = await bobCtx.newPage()
  for (const [who, p] of [['admin', admin], ['bob', bob]]) p.on('pageerror', (e) => console.log(`  [${who} pageerror]`, String(e).slice(0, 200)))
  await login(admin, 'admin@example.com')
  await login(bob, 'bob')
  check('Edge 支持 WebCodecs H.264 解码', await admin.evaluate(async () => (await VideoDecoder.isConfigSupported({ codec: 'avc1.4D401F' })).supported))

  // ---- 添加电脑：管理员输入设备码，替爸爸添加 ----
  await bob.goto(BASE + '/devices')
  await admin.goto(BASE + '/devices')
  await admin.locator('[data-testid=add-device]').click()
  // 配置串是打开窗口后才去要的：等它填上（inputValue 不等，拿到的是当下的值）
  const setupCode = (await until(() => admin.locator('[data-testid=setup-code]').inputValue({ timeout: 1000 }).catch(() => ''), 10000)) ?? ''
  check('添加向导给出配置串', setupCode.startsWith('PCA1.'), setupCode.slice(0, 12))
  await admin.locator('[data-testid=device-code]').fill(code ?? '')
  const foundText = await admin.locator('[data-testid=device-found]').textContent({ timeout: 10000 }).catch(() => '')
  check('输入设备码后显示 Agent 报上来的电脑名', foundText.includes('E2E 测试电脑'), foundText)
  await admin.locator('[data-testid=device-owner]').click()
  await admin.locator('.el-select-dropdown__item', { hasText: '爸爸' }).click()
  await admin.locator('[data-testid=confirm-device]').click()

  const online = await until(() => fs.existsSync(path.join(dir, 'state.txt')) && fs.readFileSync(path.join(dir, 'state.txt'), 'utf8') === 'Online', 20000)
  check('Agent 拿到令牌、连上服务器', !!online, agent.out.slice(-400))
  const configText = fs.readFileSync(path.join(dir, 'config.json'), 'utf8')
  check('config.json 里只有 DPAPI 加密过的令牌，没有明文', configText.includes('DeviceTokenProtected') && !/"DeviceToken"\s*:/.test(configText) && !/"AccessClientSecret"\s*:/.test(configText))
  const card = admin.locator('[data-testid=device]', { hasText: 'E2E 测试电脑' })
  check('管理员的设备列表里显示爸爸的电脑在线（实时）',
    await card.locator('[data-testid=device-online]', { hasText: '在线' }).waitFor({ timeout: 10000 }).then(() => true, () => false)
    && (await card.textContent()).includes('爸爸的电脑'))
  const bobCard = bob.locator('[data-testid=device]', { hasText: 'E2E 测试电脑' })
  check('爸爸（主人）不用刷新就看到自己的电脑在线', await bobCard.locator('[data-testid=device-online]', { hasText: '在线' }).waitFor({ timeout: 10000 }).then(() => true, () => false))
  const deviceId = await card.getAttribute('data-device-id')

  // ---- 远程控制（Agent 自动同意）----
  // 提醒只显示几秒：发起之前就开始等，不然后面几步稍慢一点就错过了
  const reminded = bob.locator('.el-notification', { hasText: '正在控制你的电脑' }).waitFor({ timeout: 30000 }).then(() => true, () => false)
  await card.locator('[data-testid=device-control]').click()
  await admin.waitForURL(/\/remote\//, { timeout: 10000 })
  const sessionId = admin.url().split('/remote/')[1]
  // 管理员诊断接口每次给出每个会话的最新快照；只取这次测试的设备和会话。
  const persistedSession = async () => {
    const response = await adminCtx.request.get(`${BASE}/api/telemetry/devices/${deviceId}?limit=5`)
    try {
      if (!response.ok()) throw new Error(`读取本次远程诊断失败（HTTP ${response.status()}）`)
      return (await response.json()).sessions.find((s) => s.sessionId === sessionId)
    } finally { await response.dispose() }
  }
  let originalViewer, freshViewer, connectedPublisher
  const streaming = await until(async () => (await admin.locator('[data-testid=remote-stats]').textContent().catch(() => '')).includes('1280×720'), 20000)
  check('对方同意后画面开始传输（1280×720，默认的自动画质）', !!streaming, await admin.locator('[data-testid=remote-stats]').textContent().catch(() => ''))
  await sleep(800)
  const colors = { tl: await pixel(admin, 0.25, 0.2), tr: await pixel(admin, 0.75, 0.2), bl: await pixel(admin, 0.25, 0.8), br: await pixel(admin, 0.75, 0.8) }
  check('解码出的画面就是测试画面（左上红、右上绿、左下蓝、右下白）',
    near(colors.tl, [255, 0, 0]) && near(colors.tr, [0, 255, 0]) && near(colors.bl, [0, 0, 255]) && near(colors.br, [255, 255, 255]), colors)
  check('爸爸收到"正在被远程"的提醒', await reminded)
  if (REALTIME) {
    // 第一份画面配置里就带着"有快速线路"（被控端等问到了才发），不是先发一份没有的、之后再补发
    const asked = agent.out.match(/服务器开了快速线路（问了 (\d+) ms）/)
    check('被控端问清了服务器有快速线路才发第一份画面配置（没有补发）', !!asked && !agent.out.includes('补发一次画面配置'), asked?.[0])
  }
  check('爸爸的设备卡片上显示谁在远程', await bobCard.locator('[data-testid=device-session]', { hasText: '控制' }).waitFor({ timeout: 10000 }).then(() => true, () => false))
  check('Agent 显示红条', agent.out.includes('红条：正在被 admin 远程控制'))
  if (tls) {
    check('被控端用的是程序自带的 TLS 1.3（不是系统的）', /连接方式 https:\/\/localhost:\d+：程序自带的 TLS 1\.3（BouncyCastle \d+\.\d+/.test(agent.out), agent.out.match(/连接方式[^\r\n]*/)?.[0])
    const all = [...tls.seen.requests, ...tls.seen.upgrades]
    const paths = {
      code: tls.seen.requests.some((r) => r.path === '/api/auth/device/code'),
      negotiate: tls.seen.requests.some((r) => r.path === '/hubs/agent/negotiate'),
      consent: tls.seen.requests.some((r) => /^\/api\/remote\/sessions\/[^/]+\/consent$/.test(r.path)),
      hub: tls.seen.upgrades.some((u) => u.path === '/hubs/agent'),
      video: tls.seen.upgrades.some((u) => u.path.startsWith('/ws/remote/')),
    }
    check('申请设备码、长连接、同意、远程画面通道都经过 TLS 入口，而且全是 TLS 1.3',
      Object.values(paths).every(Boolean) && all.length > 0 && all.every((x) => x.tls === 'TLSv1.3'),
      { ...paths, versions: [...new Set(all.map((x) => x.tls))], hosts: [...new Set(all.map((x) => x.host))] })
  }
  const stats = await until(async () => {
    const t = await admin.locator('[data-testid=remote-stats]').textContent()
    const fps = Number(t.match(/(\d+) fps/)?.[1] ?? 0)
    return fps >= 5 ? t : null
  }, 10000)
  check('持续出帧（解码帧率 ≥ 5，有往返延迟）', !!stats && / \d+ ms/.test(stats), stats)
  if (ENCODER === 'hardware') {
    // 强制硬件编码：被控端每次建编码器的那一行都写着一个（硬件）编码器，中途没有出错换回软件编码器
    const built = encodeLines(agent).map((l) => ({ line: l, ...pipelineOf(l) }))
    check('--encoder hardware：被控端用的是显卡的硬件编码器（"编码 …"那一行写着一个（硬件）编码器），没有换回软件编码器',
      built.length > 0 && built.every((b) => b.encoder?.endsWith('（硬件）')) && !agent.out.includes('改用软件编码器'),
      { built: built.map((b) => b.encoder), notes: agent.out.match(/(编码器：|硬件编码器)[^\r\n]*/g)?.slice(0, 4) ?? [] })
  }

  // 声音：测试画面配的是一直响的 440 Hz 测试音（不碰声卡）；网页默认开着声音
  const soundButton = admin.locator('[data-testid=remote-sound]')
  const audioNow = async () => ({
    state: await soundButton.getAttribute('data-audio'),
    scheduled: Number(await soundButton.getAttribute('data-scheduled')),
    peak: Number(await soundButton.getAttribute('data-peak')),
    label: (await soundButton.textContent()).trim(),
  })
  const heard = await until(async () => {
    const a = await audioNow()
    return a.scheduled > 48000 && a.peak > 0.1 ? a : null
  }, 15000)
  check('能听到对方电脑的声音（Opus 解出来超过 1 秒，不是静音，正在播放）', !!heard && heard.state === 'playing' && heard.label === '声音：开', heard ?? (await audioNow()))
  check('被控端应控制端的要求才开始采集声音', agent.out.includes('控制端要听这台电脑的声音') && agent.out.includes('声音采集已打开'))
  await soundButton.click()
  const off = await until(() => agent.out.includes('控制端关了声音'), 5000)
  await sleep(1200)
  const quiet1 = (await audioNow()).scheduled
  await sleep(1500)
  const quiet2 = await audioNow()
  check('关掉声音：被控端停止采集，网页不再收到声音', !!off && quiet2.scheduled === quiet1 && quiet2.label === '声音：关' && quiet2.state === 'off', { off: !!off, quiet1, quiet2 })
  await soundButton.click()
  const again = await until(async () => {
    const a = await audioNow()
    return a.state === 'playing' && a.scheduled > 24000 ? a : null
  }, 10000)
  check('再打开又有声音', !!again, again ?? (await audioNow()))
  const netLog = agent.out.match(/网络：目标码率 (\d+) kbps（上限 (\d+)），线路往返 (\d+) ms/)
  // 上限是自动画质的 8 Mbps
  check('被控端在按网络状况定码率（本机直连：往返很短，码率没有被压到最低）', !!netLog && Number(netLog[1]) >= 300 && Number(netLog[2]) === 8000 && Number(netLog[3]) < 200, netLog?.[0])
  check('网页上有画质菜单（被控端认识 SetVideo），没有老版本的两档按钮',
    (await admin.locator('[data-testid=remote-video]').count()) === 1 && (await admin.locator('[data-testid=remote-quality-smooth]').count()) === 0,
    { menu: await admin.locator('[data-testid=remote-video]').textContent().catch(() => null), legacy: await admin.locator('[data-testid=remote-quality-smooth]').count() })
  /** 统计上挂着的每帧采集 / 编码耗时、标志位、鼠标停上去看到的说明；有没有"忙不过来"的提示 */
  const statsEl = admin.locator('[data-testid=remote-stats]')
  const statsAttr = (name) => statsEl.getAttribute(name, { timeout: 1000 }).catch(() => null)
  const perFrame = async () => ({
    capture: Number(await statsAttr('data-capture-ms')), encode: Number(await statsAttr('data-encode-ms')),
    flags: Number(await statsAttr('data-flags')), title: await statsAttr('title'), busy: await admin.locator('[data-testid=remote-busy]').count(),
  })
  /** 盯着看 ms 毫秒：每 500 ms 记一次画布尺寸、解码帧率、统计里的每帧耗时和标志、有没有"忙不过来"的提示 */
  const watch = async (ms) => {
    const from = Date.now()
    const seen = []
    while (Date.now() - from < ms) {
      const p = await perFrame()
      seen.push({ at: Date.now() - from, size: await canvasSize(admin), fps: await shownFps(admin), capture: p.capture, encode: p.encode, flags: p.flags, busy: p.busy })
      await sleep(500)
    }
    return seen
  }
  const busyBadge = admin.locator('[data-testid=remote-busy]')
  /** "忙不过来"的提示上写的什么、鼠标停上去看到什么（没有提示时都是 null） */
  const badgeNow = async () => ({
    badge: (await busyBadge.textContent({ timeout: 500 }).catch(() => null))?.trim() ?? null,
    badgeTitle: await busyBadge.getAttribute('title', { timeout: 500 }).catch(() => null),
  })
  const timed = await until(async () => { const p = await perFrame(); return p.encode > 0 && /采集 [\d.]+ ms，编码 [\d.]+ ms/.test(p.title ?? '') ? p : null }, 10000)
  // 只看有没有报上来；忙不忙看下面专门的一段（这台电脑这会儿忙的话，测试画面也可能被标成忙不过来）
  check('统计里带着对方电脑每帧的采集、编码耗时（鼠标停在统计上能看到）', !!timed, timed ?? (await perFrame()))
  // 新网页一连上就告诉被控端"跟不上时我会说"，被控端从此按最高 30 帧发（没说过的是旧网页，按 20 帧）
  const hellos = () => (agent.out.match(/控制端跟不上时会说：按最高 30 帧发/g) ?? []).length
  check('控制端连上后告诉被控端跟不上时会说，被控端按最高 30 帧发', hellos() >= 1, agent.out.match(/控制端(跟不上|解码)[^\r\n]*/)?.[0])

  const lineSwitch = admin.locator('[data-testid=remote-line-fast]')
  if (!REALTIME) {
    check('被控端关掉了快速线路：网页上不出现线路开关', (await lineSwitch.count()) === 0 && agent.out.includes('快速线路已用环境变量'))
  } else {
    // ---- 快速线路：画面和声音经（假的）Cloudflare Realtime，不经我们的中继 ----
    const statsLine = admin.locator('[data-testid=remote-stats]')
    const lineNow = () => statsLine.getAttribute('data-line')
    const fpsNow = async () => Number((await statsLine.textContent().catch(() => '')).match(/(\d+) fps/)?.[1] ?? 0)
    check('对方的程序支持快速线路时出现线路开关；选的是标准线路时画面仍走中继',
      await lineSwitch.waitFor({ timeout: 10000 }).then(() => true, () => false) && (await lineNow()) === 'relay' && !agent.out.includes('控制端要走快速线路'))

    await lineSwitch.click()
    const fast = await until(async () => (await lineNow()) === 'realtime', 25000)
    check('切到快速线路：被控端发布、网页拉流，画面改从那边来', !!fast && agent.out.includes('快速线路已接通'),
      { line: await lineNow(), agent: agent.out.match(/快速线路[^\r\n]*/g) })
    const confirmed = await until(() => agent.out.includes('控制端已经在快速线路上收到画面'), 10000)
    await sleep(1500) // 确认之前发出、还在路上的那几帧
    const quietFrom = { ...relayed }
    await sleep(2500)
    check('控制端确认接上之后，中继上不再有画面帧和声音帧', !!confirmed && relayed.video === quietFrom.video && relayed.audio === quietFrom.audio,
      { video: relayed.video - quietFrom.video, audio: relayed.audio - quietFrom.audio })
    const fastColors = { tl: await pixel(admin, 0.25, 0.2), tr: await pixel(admin, 0.75, 0.2), bl: await pixel(admin, 0.25, 0.8), br: await pixel(admin, 0.75, 0.8) }
    // 不看画布的像素尺寸：假 SFU 是把画面解出来重新编码再转发的，分辨率从小往大爬；真的 Cloudflare 原样转发
    check('快速线路上的画面就是测试画面',
      near(fastColors.tl, [255, 0, 0]) && near(fastColors.tr, [0, 255, 0]) && near(fastColors.bl, [0, 0, 255]) && near(fastColors.br, [255, 255, 255]),
      { ...fastColors, size: await canvasSize(admin) })
    const fastStats = await until(async () => ((await fpsNow()) >= 5 && (await statsLine.textContent()).includes('快速线路') ? statsLine.textContent() : null), 10000)
    check('快速线路上持续出帧，工具栏标明走的是快速线路', !!fastStats, fastStats ?? (await statsLine.textContent()))
    const heardFast = await until(async () => {
      const a = await audioNow()
      return a.state === 'playing' && a.peak > 0.05 ? a : null
    }, 15000)
    const playedAt = (await audioNow()).scheduled
    await sleep(1500)
    const playedLater = (await audioNow()).scheduled
    check('快速线路上能听到对方电脑的声音（在放，不是静音）', !!heardFast && playedLater - playedAt > 24000, { heard: heardFast ?? (await audioNow()), played: playedLater - playedAt })
    const inputsBefore = inputs(agent).length
    await moveOnCanvas(admin, 0.3, 0.3)
    await moveOnCanvas(admin, 0.6, 0.6)
    const moved = await until(() => inputs(agent).length > inputsBefore, 5000)
    check('走快速线路时键鼠照常（它们仍走中继）', !!moved)
    const fastLog = await until(() => agent.out.match(/网络：目标码率 (\d+) kbps（上限 (\d+)），线路往返 \d+ ms（快速线路）/), 8000)
    check('被控端改用快速线路自己的码率控制', !!fastLog && Number(fastLog[1]) >= 300 && Number(fastLog[2]) === 8000, fastLog?.[0])

    // 在快速线路上换画质：被控端重建编码器，从关键帧接着发，线路不变。先换成自定义、分辨率选 960×600
    // （测试画面 1280x720 按比例缩成 960x540，码率上限是自定义默认的 8 Mbps），再换成流畅（1280x720 放得下，上限 4 Mbps），
    // 最后换回默认的自动（后面的用例按自动跑）
    const switchFrom = agent.out.length
    let rebuilt = agent.out.length
    await pickVideo(admin, 'custom')
    const customRow = await admin.locator('[data-testid=remote-video-custom-row]').waitFor({ timeout: 5000 }).then(() => true, () => false)
    await pickOption(admin, 'remote-video-res', '960×600')
    const custom960 = await until(() => /编码 960x540 30 fps，码率上限 8000 kbps/.test(agent.out.slice(rebuilt)), 8000)
    await sleep(2000)
    const customOk = customRow && !!custom960 && (await lineNow()) === 'realtime' && (await fpsNow()) >= 5 && (await statsLine.textContent()).includes('快速线路')
    rebuilt = agent.out.length
    await pickVideo(admin, 'smooth')
    const smooth = await until(() => /编码 1280x720 30 fps，码率上限 4000 kbps/.test(agent.out.slice(rebuilt)), 8000)
    await sleep(2000)
    const smoothOk = !!smooth && (await lineNow()) === 'realtime' && (await fpsNow()) >= 5
    const rowClosed = (await admin.locator('[data-testid=remote-video-custom-row]').count()) === 0
    rebuilt = agent.out.length
    await pickVideo(admin, 'auto')
    const auto = await until(() => /编码 1280x720 30 fps，码率上限 8000 kbps/.test(agent.out.slice(rebuilt)), 8000)
    // 鼠标从工具栏上挪开：提示条出现在顶部正中，鼠标停在它上面的话它不会自己消失，还会挡住下面要点的按钮
    await moveOnCanvas(admin, 0.5, 0.5)
    await sleep(2000)
    check('快速线路上换画质（自定义 960×600 → 流畅 → 自动）：编码器重建后画面接着出，仍在快速线路上，中继上没有画面帧',
      customOk && smoothOk && rowClosed && !!auto && (await lineNow()) === 'realtime' && (await fpsNow()) >= 5 && relayed.video === quietFrom.video,
      { customRow, custom: !!custom960, smooth: !!smooth, rowClosedAfterPreset: rowClosed, auto: !!auto, line: await lineNow(), fps: await fpsNow(),
        relayFrames: relayed.video - quietFrom.video, agent: encodeLines(agent, switchFrom) })

    // 假 SFU 那一头：被控端发来的画面在解；故意丢掉的 2% 的包靠重发补上了
    const publisher = (await sfu.summary()).find((s) => s.published.length > 0 && s.state === 'connected')
    const received = async () => (await sfu.stats(publisher.id)).inbound.find((t) => t.kind === 'video')
    const sfuBefore = await received()
    await sleep(3000)
    const sfuAfter = await received()
    // 重发管用的标志：对面要过包（NACK）、画面一直在解，而且没有因为缺包去要关键帧（PLI 没涨）。
    // 不看分辨率：假 SFU 转码后发给网页的码率很低，被控端按"收到的比发出的少得多"降了码率，自动画质会跟着降一档分辨率
    check('被控端发到 SFU 的画面一直在解；故意丢的包靠重发补上了，不用重新要关键帧',
      sfuAfter.framesDecoded - sfuBefore.framesDecoded >= 20 && sfuAfter.nackCount > 0 && sfuAfter.pliCount === sfuBefore.pliCount && sfuAfter.frameWidth >= 960,
      { decoded: sfuAfter.framesDecoded - sfuBefore.framesDecoded, nack: sfuAfter.nackCount, lost: sfuAfter.packetsLost, pli: [sfuBefore.pliCount, sfuAfter.pliCount], freezes: sfuAfter.freezeCount, size: `${sfuAfter.frameWidth}x${sfuAfter.frameHeight}` })

    // ---- 线路不自动换：快速线路出了问题只提示，重新接的也是这条线路；换到标准线路只能是用户自己点 ----
    const lineProblem = admin.locator('[data-testid=remote-line-problem]')
    const ended = () => admin.locator('[data-testid=remote-ended]').isVisible().catch(() => false)
    /** 假 SFU 上网页这一头现在连着的那条（之前的窗口留下的旧连接可能还没来得及显示成断开：取最新的） */
    const liveSubscriber = async () => (await sfu.summary()).filter((s) => s.pulled > 0 && s.state === 'connected').at(-1)

    // 默认线路：一个从没选过线路的浏览器接手这次远程——默认就是快速线路，而且从头到尾只画这条线路的画面，
    // 不先出标准线路的再换过去（中继上这期间有画面帧过来，不解不画）
    const freshCtx = await browser.newContext({ ...opts, storageState: { cookies: (await adminCtx.storageState()).cookies, origins: [] } })
    await freshCtx.addInitScript(() => {
      const original = CanvasRenderingContext2D.prototype.drawImage
      window.__draws = { relay: 0, fast: 0 }
      CanvasRenderingContext2D.prototype.drawImage = function (source, ...rest) {
        if (source instanceof HTMLVideoElement) window.__draws.fast++
        else window.__draws.relay++ // 中继的画面是 WebCodecs 解出来的 VideoFrame
        return original.call(this, source, ...rest)
      }
    })
    const fresh = await freshCtx.newPage()
    let freshRelayFrames = 0
    fresh.on('websocket', (ws) => {
      if (!ws.url().endsWith('/controller')) return
      ws.on('framereceived', ({ payload }) => {
        if (Buffer.isBuffer(payload) && payload[0] === 0x02) freshRelayFrames++
      })
    })
    await fresh.goto(admin.url())
    await admin.locator('[data-testid=remote-replaced]').waitFor({ timeout: 15000 })
    // 被替换的窗口会立即上报。先取从设备页点击、先后播放了两种线路的这份，避免被新窗口覆盖。
    originalViewer = await until(async () => {
      const v = (await persistedSession())?.viewer
      return v?.startup?.firstFrameLine === 'relay' && v.playback?.fastFramesDecoded > 0 ? v : null
    }, 12000, 500)
    const freshLine = () => fresh.locator('[data-testid=remote-stats]').getAttribute('data-line', { timeout: 500 }).catch(() => null)
    const freshFast = await until(async () => (await freshLine()) === 'realtime', 30000)
    await sleep(1500)
    const draws = await fresh.evaluate(() => window.__draws)
    const stored = await fresh.evaluate(() => localStorage.getItem('remote.transport'))
    check('没选过线路的浏览器默认走快速线路，从头只画这条线路的画面（中继上来的画面帧不画）',
      !!freshFast && draws.fast > 0 && draws.relay === 0 && freshRelayFrames > 0 && stored === null,
      { line: await freshLine(), draws, relayFramesIgnored: freshRelayFrames, stored })
    // 回到原来的窗口（它选的也是快速线路）：按它的选择重新接上
    await admin.locator('[data-testid=remote-take-over]').click()
    await fresh.locator('[data-testid=remote-replaced]').waitFor({ timeout: 15000 })
    freshViewer = await until(async () => {
      const v = (await persistedSession())?.viewer
      return v?.startup?.firstFrameLine === 'realtime' ? v : null
    }, 12000, 500)
    await freshCtx.close()
    const backOnFast = await until(async () => (await lineNow()) === 'realtime', 30000)
    check('原来的窗口接手回来，按它选的快速线路重新接上', !!backOnFast && (await until(async () => (await fpsNow()) >= 5, 10000)) !== null, { line: await lineNow() })

    // 掐掉 SFU 上网页这一头的连接：网页收不到画面，不拿中继的顶上；画面上说明原因，自己重新接上；会话不断
    const subscriber = await liveSubscriber()
    await sfu.drop(subscriber.id)
    const broke = await until(async () => (await lineNow()) === 'connecting', 20000, 100)
    const toldText = await lineProblem.waitFor({ timeout: 5000 }).then(() => lineProblem.textContent(), () => '')
    const seen = new Set()
    const reconnected = await until(async () => {
      const now = await lineNow()
      seen.add(now)
      return now === 'realtime'
    }, 40000, 100)
    check('快速线路断了：不换到标准线路，画面上说明原因，自己重新接上这条线路，会话不断',
      !!broke && toldText.includes('快速线路接不上') && !!reconnected && !seen.has('relay')
        && (await until(async () => (await fpsNow()) >= 5, 10000)) !== null && (await lineProblem.count()) === 0 && !(await ended()),
      { broke: !!broke, told: toldText, reconnected: !!reconnected, lines: [...seen] })

    // 页面进了后台（切到别的标签页、窗口被盖住）：浏览器不再回调"出了一帧"，这段时间不能算成"没有画面"；
    // 线路要是真断了，后台时不去重新接（接上了也等不到第一帧，白算一次失败），回到前台马上接。
    // 无界面浏览器做不出真正的后台，这里只让页面"以为"自己在后台：验证的是后台时不接、回前台就接这两步
    const setHidden = (hidden) => admin.evaluate((hidden) => {
      Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => (hidden ? 'hidden' : 'visible') })
      document.dispatchEvent(new Event('visibilitychange'))
    }, hidden)
    await setHidden(true)
    await sleep(9000) // 比"7 秒没有画面算断"长：在后台待着本身不会把线路判成断了
    const stayed = (await lineNow()) === 'realtime'
    const subscriber2 = await liveSubscriber()
    await sfu.drop(subscriber2.id)
    const broke2 = await until(async () => (await lineNow()) === 'connecting', 45000)
    await sleep(8000) // 断了之后 2 秒就会重新接：页面在后台，不接
    const waited = (await lineNow()) === 'connecting'
    await setHidden(false)
    const resumed = await until(async () => (await lineNow()) === 'realtime', 15000)
    check('页面在后台：待着不算断线；线路真断了不在后台重新接，回到前台马上接上',
      stayed && !!broke2 && waited && !!resumed && (await until(async () => (await fpsNow()) >= 5, 10000)) !== null,
      { stayed, broke: !!broke2, waitedInBackground: waited, resumed: !!resumed, line: await lineNow() })

    // 手动切到标准线路（工具栏）：被控端收掉快速线路，画面和声音回到中继，不再去接快速线路
    let mark = agent.out.length
    await admin.locator('[data-testid=remote-line-standard]').click()
    const standard = await until(async () => (await lineNow()) === 'relay' && agent.out.slice(mark).includes('控制端要求改回中继'), 10000)
    const before2 = relayed.video
    await sleep(1500)
    check('手动切到标准线路：画面回到中继，不再去接快速线路',
      !!standard && relayed.video > before2 && (await fpsNow()) >= 5 && !agent.out.slice(mark).includes('控制端要走快速线路'), { line: await lineNow(), relayFrames: relayed.video - before2 })
    const soundBack = await until(async () => {
      const a = await audioNow()
      return a.state === 'playing' && a.peak > 0.1 ? a : null
    }, 10000)
    check('回到标准线路后声音也回到中继', !!soundBack, soundBack ?? (await audioNow()))
    // 快速线路上因为码率低（自动画质）降过的分辨率，回到中继后按中继自己的情况重新判断
    const fullSize = await until(async () => (await canvasSize(admin)) === '1280x720', 10000)
    // 没回到 1280×720 时看是哪样拦住的：这次远程里自动画质因为电脑忙降过（之后往回升要先估每帧用时），还是网络那边
    check('回到标准线路后画面是原来的 1280×720', !!fullSize,
      { size: await canvasSize(admin), perFrame: await perFrame(), auto: agent.out.match(/自动画质：[^\r\n]*/g)?.slice(-6),
        agent: agent.out.slice(mark).match(/(编码 \d+x\d+|目标码率 \d+ kbps)[^\r\n]*/g)?.slice(-6) })
    // 再手动切回快速线路：后面要看它彻底接不上时怎么办
    await lineSwitch.click()
    const fastAgain = await until(async () => (await lineNow()) === 'realtime', 25000)
    check('手动切回快速线路：重新接上', !!fastAgain && (await until(async () => (await fpsNow()) >= 5, 10000)) !== null, { line: await lineNow() })

    // 快速线路根本接不上了（把假 SFU 停掉，相当于服务器连不上 Cloudflare）：重新接几次都不行就停下来，
    // 画面上给出"重试"和"切到标准线路"。这期间中继上有画面帧过来，但不画——线路不自动换
    // Agent 每分钟上报一次；下面故意产生的一连串失败可能把成功记录挤出最近八次的窗口。
    connectedPublisher = await until(async () =>
      (await persistedSession())?.agent?.publisherAttempts?.find((a) => a.outcome === 'connected'), 65000, 1000)
    await sfu.close()
    sfu = null
    sfuStoppedAt = new Date().toISOString()
    const broke3 = await until(async () => (await lineNow()) === 'connecting', 40000)
    const relayBefore = relayed.video
    const noPicture = await until(async () => (await fpsNow()) === 0, 15000)
    const retry = admin.locator('[data-testid=remote-line-retry]')
    const seen3 = new Set()
    const gaveUp = await until(async () => {
      seen3.add(await lineNow())
      return (await admin.locator('[data-testid=remote-line-problem][data-retrying=false]').count()) > 0
    }, 120000, 250)
    const stillNoPicture = (await fpsNow()) === 0
    check('快速线路接不上：不自动换到标准线路（中继上有画面帧来也不画），试几次之后停下来等用户决定，会话不断',
      !!broke3 && !!noPicture && !!gaveUp && stillNoPicture && relayed.video > relayBefore && !seen3.has('relay') && !seen3.has('realtime') && !(await ended()),
      { broke: !!broke3, noPicture: !!noPicture, gaveUp: !!gaveUp, relayFramesIgnored: relayed.video - relayBefore, lines: [...seen3], told: await lineProblem.textContent().catch(() => '') })
    // 点"重试"：再接一轮（还是接不上），线路仍然没有自己换
    mark = agent.out.length
    await retry.click()
    const retried = await until(() => agent.out.slice(mark).includes('控制端要走快速线路'), 10000)
    const failedAgain = await until(async () => (await admin.locator('[data-testid=remote-line-problem][data-retrying=true]').count()) > 0, 20000)
    check('点"重试"：重新去接快速线路，接不上照样只是提示', !!retried && !!failedAgain && (await lineNow()) === 'connecting',
      { retried: !!retried, told: await lineProblem.textContent().catch(() => ''), line: await lineNow() })

    // 点画面上的"切到标准线路"：画面回到中继，记住这个选择，不再去接快速线路；后面的用例都在中继上跑
    mark = agent.out.length
    await admin.locator('[data-testid=remote-line-use-standard]').click()
    const standardNow = await until(async () => (await lineNow()) === 'relay', 10000)
    const picture = await until(async () => (await fpsNow()) >= 5, 15000)
    await sleep(6000) // 之前排着的"重新接"不能再生效
    check('快速线路接不上时点"切到标准线路"：画面回到中继，记住了这个选择，不再去接快速线路',
      !!standardNow && !!picture && (await lineNow()) === 'relay' && (await admin.locator('[data-testid=remote-line-wait]').count()) === 0
        && (await admin.evaluate(() => localStorage.getItem('remote.transport'))) === 'relay' && !agent.out.slice(mark).includes('控制端要走快速线路'),
      { line: await lineNow(), fps: await fpsNow(), stored: await admin.evaluate(() => localStorage.getItem('remote.transport')) })
    const soundAfter = await until(async () => {
      const a = await audioNow()
      return a.state === 'playing' && a.peak > 0.1 ? a : null
    }, 10000)
    check('这样切到标准线路后声音也回到中继', !!soundAfter, soundAfter ?? (await audioNow()))
  }

  // 同一会话在另一个静音窗口接手，再回到原窗口。检查线上的声音帧，而非只看按钮。
  const mutedContext = await browser.newContext({ ...opts, storageState: await adminCtx.storageState() })
  await standardLine(mutedContext)
  const muted = await mutedContext.newPage()
  await muted.addInitScript(() => localStorage.setItem('remote.sound', '0'))
  let audioPackets = 0
  let videoPackets = 0
  muted.on('websocket', (ws) => {
    if (!ws.url().endsWith('/controller')) return
    ws.on('framereceived', ({ payload }) => {
      if (!Buffer.isBuffer(payload)) return
      if (payload[0] === 0x08) audioPackets++
      if (payload[0] === 0x02) videoPackets++
    })
  })
  const hellosBefore = hellos()
  await muted.goto(admin.url())
  await admin.locator('[data-testid=remote-replaced]').waitFor()
  await muted.locator('[data-testid=remote-sound][data-audio=off]').waitFor()
  const beforeMute = audioPackets
  const beforeVideo = videoPackets
  await sleep(1200)
  check('静音窗口接手后只收到画面，声音停止传输', videoPackets > beforeVideo && audioPackets === beforeMute,
    { videoPackets: videoPackets - beforeVideo, audioPackets: audioPackets - beforeMute })
  const helloOnTakeover = await until(() => hellos() > hellosBefore, 5000)
  await admin.locator('[data-testid=remote-take-over]').click()
  await muted.locator('[data-testid=remote-replaced]').waitFor()
  const heardAfterTakeover = await until(async () => (await audioNow()).scheduled > 24000, 10000)
  check('有声窗口重新接手后继续收到测试音', !!heardAfterTakeover)
  const helloOnTakeBack = await until(() => hellos() > hellosBefore + 1, 5000)
  check('换了窗口接手（两次）：新窗口每次都重新告诉被控端跟不上时会说', !!helloOnTakeover && !!helloOnTakeBack, { before: hellosBefore, now: hellos() })
  await mutedContext.close()

  // 键鼠
  const before = inputs(agent).length
  await moveOnCanvas(admin, 0.5, 0.5)
  await admin.mouse.down()
  await admin.mouse.up()
  await admin.mouse.wheel(0, 100)
  await admin.keyboard.press('a')
  await admin.keyboard.press('Shift+ArrowLeft')
  const got = await until(() => {
    const list = inputs(agent).slice(before)
    return list.some((e) => e.t === 'key' && e.scan === 0x2a && !e.down) ? list : null // Shift 最后松开
  }, 5000)
  const events = got ?? inputs(agent).slice(before)
  const move = [...events].reverse().find((e) => e.t === 'move')
  check('鼠标移到画面中央 → 对方电脑上 (640, 360) 附近', !!move && Math.abs(move.x - 640) <= 4 && Math.abs(move.y - 360) <= 4, move)
  check('点击：左键按下、松开', events.some((e) => e.t === 'button' && e.button === 'Left' && e.down) && events.some((e) => e.t === 'button' && e.button === 'Left' && !e.down))
  check('滚轮向下一格 → -120', events.some((e) => e.t === 'wheel' && e.dy === -120), events.filter((e) => e.t === 'wheel'))
  check('按键按物理键位发扫描码：A=0x1E、Shift=0x2A、←=0xE04B（扩展键）',
    [0x1e, 0x2a, 0xe04b].every((s) => events.some((e) => e.t === 'key' && e.scan === s && e.down) && events.some((e) => e.t === 'key' && e.scan === s && !e.down)))

  await admin.locator('[data-testid=remote-type]').click()
  await admin.locator('[data-testid=remote-text]').fill('你好，世界')
  await admin.locator('[data-testid=remote-text]').press('Enter')
  await admin.locator('button', { hasText: '特殊键' }).click()
  await admin.locator('.el-dropdown-menu__item', { hasText: 'Win 键' }).click()
  const typed = await until(() => {
    const list = inputs(agent)
    return list.some((e) => e.t === 'text' && e.text === '你好，世界') && list.some((e) => e.t === 'key' && e.scan === 0xe05b) ? list : null
  }, 5000)
  check('文字输入框发中文（输入法打好的字）', !!typed?.some((e) => e.t === 'text' && e.text === '你好，世界'))
  check('特殊键：Win 键', !!typed?.some((e) => e.t === 'key' && e.scan === 0xe05b && e.down))

  // 点在画面外的黑边上：不在对方电脑上点击
  const beforeLetterbox = inputs(agent).length
  const canvasBox = await admin.locator('[data-testid=remote-canvas]').boundingBox()
  await admin.mouse.click(canvasBox.x + canvasBox.width / 2, canvasBox.y + 4) // 1280×720 的画面在更高的区域里：上下有黑边
  await sleep(800)
  check('点在黑边上不会点到对方电脑', !inputs(agent).slice(beforeLetterbox).some((e) => e.t === 'button'), inputs(agent).slice(beforeLetterbox))

  // 粘贴：本机剪贴板的文字先送到对方剪贴板
  await admin.locator('[data-testid=remote-stage]').focus()
  await admin.evaluate(() => {
    const dt = new DataTransfer()
    dt.setData('text/plain', '粘贴内容')
    document.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt }))
  })
  check('粘贴时把本机剪贴板的文字写到对方剪贴板', !!(await until(() => agent.out.includes('剪贴板写入 4 字'), 5000)))

  // 画质、切换显示器
  let videoMark = agent.out.length
  await pickVideo(admin, 'clear')
  await admin.locator('[data-testid=remote-monitor]').click()
  await admin.locator('.el-select-dropdown__item', { hasText: '显示器 2' }).click()
  const second = await until(async () => (await canvasSize(admin)) === '1920x1080' && near(await pixel(admin, 0.75, 0.8), [255, 255, 0]), 15000)
  check('清晰 + 切到第二块显示器：1920×1080，右下象限是黄色', !!second, { size: await canvasSize(admin), br: await pixel(admin, 0.75, 0.8), agent: encodeLines(agent, videoMark) })
  const before2 = inputs(agent).length
  await moveOnCanvas(admin, 0.5, 0.5)
  const move2 = await until(() => inputs(agent).slice(before2).reverse().find((e) => e.t === 'move'), 5000)
  check('第二块显示器上的坐标换算到虚拟桌面（1280 + 960, 540）', !!move2 && Math.abs(move2.x - 2240) <= 4 && Math.abs(move2.y - 540) <= 4, move2)

  // 原画：第二块显示器按原始的 1920×1080 编码，码率上限 16 Mbps
  videoMark = agent.out.length
  await pickVideo(admin, 'original')
  const original = await until(() => /编码 1920x1080 30 fps，码率上限 16000 kbps/.test(agent.out.slice(videoMark)), 8000)
  const originalShown = await until(async () => (await canvasSize(admin)) === '1920x1080' && (await shownFps(admin)) >= 5, 8000)
  check('原画：第二块显示器原样 1920×1080，码率上限 16 Mbps，画面接着出', !!original && !!originalShown,
    { size: await canvasSize(admin), fps: await shownFps(admin), agent: encodeLines(agent, videoMark) })

  // 自定义 1280×800 / 15 帧 / 2 Mbps：1920×1080 按比例缩进 1280×800 的框里是 1280×720；每一项改了都马上用上，并记在这台设备上
  videoMark = agent.out.length
  await pickVideo(admin, 'custom')
  const customRow = await admin.locator('[data-testid=remote-video-custom-row]').waitFor({ timeout: 5000 }).then(() => true, () => false)
  await pickOption(admin, 'remote-video-res', '1280×800')
  await pickOption(admin, 'remote-video-fps', '15 帧')
  await pickOption(admin, 'remote-video-kbps', '2 Mbps')
  const custom = await until(() => /编码 1280x720 15 fps，码率上限 2000 kbps/.test(agent.out.slice(videoMark)), 10000)
  const customShown = await until(async () => (await canvasSize(admin)) === '1280x720', 10000)
  const storedVideo = await admin.evaluate(() => { try { return JSON.parse(localStorage.getItem('remote.video') ?? 'null') } catch { return 'bad json' } })
  const want = { mode: 4, maxWidth: 1280, maxHeight: 800, fps: 15, maxKbps: 2000 }
  const isWanted = (s) => !!s && typeof s === 'object' && Object.keys(want).every((k) => s[k] === want[k])
  check('自定义 1280×800 / 15 帧 / 2 Mbps：第二块显示器缩成 1280×720 编码、每秒 15 帧、码率上限 2 Mbps；选择记在这台设备上',
    customRow && !!custom && !!customShown && isWanted(storedVideo?.current) && isWanted(storedVideo?.custom),
    { customRow, size: await canvasSize(admin), stored: storedVideo, menu: await admin.locator('[data-testid=remote-video]').textContent().catch(() => null), agent: encodeLines(agent, videoMark) })
  await admin.locator('[data-testid=remote-video-custom-close]').click()
  check('自定义那一行能收起', (await admin.locator('[data-testid=remote-video-custom-row]').count()) === 0)

  // 结束
  await admin.locator('[data-testid=remote-end]').click()
  check('结束后显示"已结束"', await admin.locator('[data-testid=remote-end-reason]', { hasText: '已结束' }).waitFor({ timeout: 10000 }).then(() => true, () => false))
  check('Agent 收起红条', !!(await until(() => agent.out.includes('红条收起'), 5000)))
  const audit = sql(`select end_reason || ',' || bytes_relayed from remote_sessions where id = '${sessionId}'`)
  check('记录里有结束原因和转发流量', audit.startsWith('controller,') && Number(audit.split(',')[1]) > 10_000, audit)

  // ---- 诊断（只有管理员有）：这台电脑的硬件和能力、刚才这次远程两边报上来的汇总 ----
  // 被控端连上服务器后在子进程里探测硬件（最多 60 秒）；远程结束时两边各报最后一份（被控端 PUT，网页 sendBeacon）
  await admin.goto(BASE + '/devices')
  await card.locator('[data-testid=device-online]', { hasText: '在线' }).waitFor({ timeout: 10000 })
  await card.locator('[data-testid=device-more]').click()
  await admin.locator('[data-testid=device-diagnostics]:visible').click()
  const diag = admin.locator('[data-testid=diagnostics-dialog]')
  const diagLoaded = await diag.locator('[data-loaded=true]').waitFor({ timeout: 15000 }).then(() => true, () => false)
  check('管理员在电脑的菜单里点"诊断"：打开诊断窗口，拉到了数据', diagLoaded,
    diagLoaded ? undefined : { alert: await diag.locator('.el-alert').textContent({ timeout: 500 }).catch(() => null) })
  /** 点"刷新"，等它拉完 */
  const refreshDiag = async () => {
    await diag.locator('[data-testid=diagnostics-refresh]').click().catch(() => undefined)
    await diag.locator('[data-testid=diagnostics-refresh].is-loading').waitFor({ state: 'detached', timeout: 10000 }).catch(() => undefined)
  }
  const cpuName = diagLoaded ? await until(async () => {
    const name = await diag.locator('[data-testid=diagnostics-cpu]').getAttribute('data-name', { timeout: 500 }).catch(() => null)
    if (name) return name
    await refreshDiag()
    return null
  }, 60000, 2000) : null
  const hardwareSeen = {
    cpu: await diag.locator('[data-testid=diagnostics-cpu]').textContent({ timeout: 500 }).catch(() => null),
    gpus: await diag.locator('[data-testid=diagnostics-gpu]').allTextContents().catch(() => []),
    displays: await diag.locator('[data-testid=diagnostics-display]').allTextContents().catch(() => []),
    problems: await diag.locator('[data-testid=diagnostics-problem]').allTextContents().catch(() => []),
    none: await diag.locator('[data-testid=diagnostics-no-snapshot]').count().catch(() => 0),
  }
  check('诊断：这台电脑的硬件（处理器型号、至少一张显卡）', !!cpuName && hardwareSeen.gpus.length >= 1 && hardwareSeen.gpus.every((g) => g.trim().length > 0), hardwareSeen)
  const encoders = await diag.locator('[data-testid=diagnostics-encoder]').evaluateAll((els) => els.map((e) => ({
    name: e.dataset.name, hardware: e.dataset.hardware, avgMs: e.dataset.testAvgMs, framesOut: e.dataset.testFramesOut,
  }))).catch(() => [])
  const softwareEncoder = encoders.find((e) => e.hardware === 'false' && Number(e.avgMs) > 0 && Number(e.framesOut) > 0)
  check('诊断：编码器里有微软的软件 H.264 编码器，带着试编的结果（每帧多少 ms、出了几帧）', !!softwareEncoder,
    { software: softwareEncoder ?? null, encoders, gdi: await diag.locator('[data-testid=diagnostics-gdi]').textContent({ timeout: 500 }).catch(() => null) })
  // 刚结束的这次远程：被控端、控制端都报了（被控端最后一份在结束后几秒内到）
  const sessionRow = diag.locator(`[data-testid=diagnostics-session][data-session-id="${sessionId}"]`)
  const sessionSeen = () => sessionRow.evaluate((e) => ({ ...e.dataset }), undefined, { timeout: 500 }).catch(() => null)
  const bothSides = diagLoaded ? await until(async () => {
    const row = await sessionSeen()
    if (row?.agent === 'true' && row.viewer === 'true') return row
    await refreshDiag()
    return null
  }, 20000, 1000) : null
  const rowNow = bothSides ?? (await sessionSeen())
  check('诊断：最近的远程里有刚才这一次，被控端和控制端都报了（发出帧率、看到的帧率都大于 0）',
    !!bothSides && Number(bothSides.sentFps) > 0 && Number(bothSides.viewerFps) > 0,
    { row: rowNow, rows: await diag.locator('[data-testid=diagnostics-session]').count().catch(() => 0) })
  // 这里跑的是 Edge，应当报成"Edge 1xx"；要紧的是只有品牌和大版本，不是整串 User-Agent
  check('诊断：控制端报的浏览器只有品牌和大版本（不是整串 User-Agent）',
    /^[A-Za-z][\w ]* \d+$/.test(rowNow?.browser ?? '') && !/Mozilla|Windows NT|AppleWebKit/.test(rowNow?.browser ?? ''), rowNow?.browser ?? null)
  if (REALTIME) {
    const nonnegative = (n) => Number.isSafeInteger(n) && n >= 0
    const a = connectedPublisher
    const stages = a && [a.iceConfigMs, a.gatherMs, a.publishMs, a.handshakeMs]
    const candidate = (v) => ['host', 'srflx', 'prflx', 'relay'].includes(v)
    check('诊断：真实被控端完成四个发布阶段，记录实际提名的两端候选类型',
      a?.strategy === 'all' && a.attempt >= 1 && stages.every(nonnegative) && nonnegative(a.totalMs) && a.totalMs > 0
        && stages.reduce((sum, n) => sum + n, 0) <= a.totalMs && candidate(a.localCandidateType) && candidate(a.remoteCandidateType), a ?? null)
    const hasFastFirstFrame = (s) => s?.attempts.some((t) => t.outcome === 'firstFrame' && nonnegative(t.publisherWaitMs)
      && nonnegative(t.pullToFirstFrameMs) && nonnegative(t.totalMs) && Math.abs(t.totalMs - t.publisherWaitMs - t.pullToFirstFrameMs) <= 1)
    const startup = originalViewer?.startup
    check('诊断：从设备页进入的浏览器记录请求到首帧、连接到首帧，以及切到快速线路后的出图耗时',
      startup?.firstFrameLine === 'relay' && nonnegative(startup.connectToFirstFrameMs) && nonnegative(startup.requestToFirstFrameMs)
        && startup.requestToFirstFrameMs >= startup.connectToFirstFrameMs && hasFastFirstFrame(startup), startup ?? null)
    const direct = freshViewer?.startup
    check('诊断：直接打开会话的接手窗口以快速线路出首帧，没有虚构点击发起请求的时刻',
      direct?.firstFrameLine === 'realtime' && direct.requestToFirstFrameMs === null && nonnegative(direct.connectToFirstFrameMs)
        && hasFastFirstFrame(direct), direct ?? null)
    const p = originalViewer?.playback
    check('诊断：Edge 实际解出了快速线路的帧，标准线路也采到了 WebCodecs 解码队列',
      p?.fastFramesDecoded > 0 && p.fastDecodeMs?.samples > 0 && p.fastDecodeMs.p90 >= p.fastDecodeMs.p50
        && p.relayDecodeQueue?.samples > 0 && p.relayDecodeQueue.p90 >= p.relayDecodeQueue.p50 && nonnegative(p.relaySkippedUntilKey), p ?? null)
    const final = await until(async () => {
      const s = await persistedSession()
      return s?.agent?.final && s.viewer?.final ? s : null
    }, 20000, 500)
    const attempts = final?.agent?.publisherAttempts ?? []
    check('诊断：最终汇总仍有两种线路的播放数据，并记录关闭假 SFU 后的发布失败',
      final?.viewer?.playback?.fastFramesDecoded > 0 && final.viewer.playback.relayDecodeQueue?.samples > 0
        && attempts.length <= 8 && attempts.some((t) => t.outcome === 'failed' && nonnegative(t.publishMs) && t.handshakeMs === null),
      { outcomes: attempts.map((t) => t.outcome), finalViewer: final?.viewer?.final ?? false })
    if (final) await refreshDiag()
  }
  if (bothSides) {
    await sessionRow.locator('xpath=ancestor::tr[1]').locator('.el-table__expand-icon').click()
    const expanded = sessionRow.locator('xpath=ancestor::tr[1]/following-sibling::tr[1]').locator('[data-testid=diagnostics-session-detail]')
    await expanded.waitFor({ timeout: 5000 })
    if (REALTIME) {
      const startup = expanded.locator('[data-testid=diagnostics-startup] dl')
      const attempts = expanded.locator('[data-testid=diagnostics-attempts] li')
      const playback = expanded.locator('[data-testid=diagnostics-playback]')
      check('诊断：展开本次会话能看到首帧计时、连接尝试和两种线路的播放指标',
        await startup.isVisible() && await attempts.first().isVisible()
          && await playback.getByRole('heading', { name: '快速线路', exact: true }).isVisible()
          && await playback.getByRole('heading', { name: '标准线路', exact: true }).isVisible())
    }
    const raw = expanded.locator('[data-testid=diagnostics-session-raw-fields]')
    if (await raw.count() && !(await raw.evaluate((e) => e.open))) await raw.locator('summary').click()
    const detail = await expanded.innerText({ timeout: 5000 }).catch(() => '')
    check('诊断：展开这一次能看到两边报的所有字段', ['sentFps', 'encodeMs', 'decodedFps', 'freezes'].every((k) => detail.includes(k)), detail.slice(0, 300))
  }
  await admin.keyboard.press('Escape')
  await diag.waitFor({ state: 'hidden', timeout: 5000 }).catch(() => undefined)
  // 手机上（360 px 宽）：整屏显示，表格在自己里面横着滚，页面不横着滚。
  // 宽度跨过 1024 px 时应用换一套布局（桌面三栏 ↔ 手机底部 Tab，各有各的 router-view），设备页重新挂上、开着的窗口也跟着没了：
  // 先换成手机的宽度、等换完布局，再从"更多"里重新打开"诊断"
  await admin.setViewportSize({ width: 360, height: 740 })
  const mobileLayout = await admin.locator('[data-testid=desktop-nav]').waitFor({ state: 'detached', timeout: 5000 }).then(() => true, () => false)
  await card.locator('[data-testid=device-more]').click()
  await admin.locator('[data-testid=device-diagnostics]:visible').click()
  const narrowDiag = admin.locator('[data-testid=diagnostics-dialog]:visible')
  const narrowLoaded = await narrowDiag.locator('[data-loaded=true]').waitFor({ timeout: 15000 }).then(() => true, () => false)
  // 等打开的动画走完
  await sleep(600)
  const narrow = await admin.evaluate(() => {
    const d = [...document.querySelectorAll('[data-testid=diagnostics-dialog]')].map((e) => e.getBoundingClientRect()).find((r) => r.width > 0 && r.height > 0)
    return { page: document.documentElement.scrollWidth, viewport: window.innerWidth, left: d ? Math.round(d.left) : null, right: d ? Math.round(d.right) : null }
  })
  check('诊断在 360 px 宽的手机上不出屏幕、页面不横着滚', narrowLoaded && narrow.page <= 360 && narrow.left !== null && narrow.left >= 0 && narrow.right <= 360,
    { ...narrow, mobileLayout, loaded: narrowLoaded })
  await admin.keyboard.press('Escape')
  await narrowDiag.waitFor({ state: 'hidden', timeout: 5000 }).catch(() => undefined)
  await admin.setViewportSize(opts.viewport)
  await admin.locator('[data-testid=desktop-nav]').waitFor({ timeout: 5000 }).catch(() => undefined)
  // 爸爸是电脑的主人，但不是管理员：菜单里有"设置"，没有"诊断"
  await bob.goto(BASE + '/devices')
  await bobCard.locator('[data-testid=device-more]').click()
  const bobMenu = await bob.locator('[data-testid=device-settings]:visible').waitFor({ timeout: 5000 }).then(() => true, () => false)
  const bobDiagnostics = await bob.locator('[data-testid=device-diagnostics]').count()
  check('诊断只给管理员：爸爸（主人、不是管理员）的菜单里没有"诊断"', bobMenu && bobDiagnostics === 0, { menuOpened: bobMenu, diagnostics: bobDiagnostics })
  // 把菜单收起来（后面还要再点开它）
  await bob.locator('h1').first().click()
  await bob.locator('[data-testid=device-settings]').waitFor({ state: 'hidden', timeout: 5000 }).catch(() => undefined)

  // ---- 只看模式：点击不会注入；画质照样能换 ----
  await admin.goto(BASE + '/devices')
  videoMark = agent.out.length
  await card.locator('[data-testid=device-view]').click()
  await admin.waitForURL(/\/remote\//)
  const viewStreaming = await until(async () => (await admin.locator('[data-testid=remote-stats]').textContent().catch(() => '')).includes('fps'), 20000)
  check('只看模式也能看到画面', !!viewStreaming)
  const before3 = inputs(agent).length
  await moveOnCanvas(admin, 0.3, 0.3)
  await admin.mouse.down()
  await admin.mouse.up()
  await admin.keyboard.press('b')
  await sleep(1500)
  check('只看模式：键鼠不会到对方电脑', inputs(agent).length === before3, inputs(agent).slice(before3))
  check('只看模式没有特殊键、输入文字按钮', (await admin.locator('[data-testid=remote-type]').count()) === 0)
  // 上一次在自定义里选的（1280×800 / 15 帧 / 2 Mbps）记在这台设备上：这一次一开始就按它来（第一块显示器 1280×720 放得下）
  const remembered = await until(() => /编码 1280x720 15 fps，码率上限 2000 kbps/.test(agent.out.slice(videoMark)), 10000)
  const viewFrom = videoMark
  videoMark = agent.out.length
  await pickVideo(admin, 'auto')
  const viewAuto = await until(() => /编码 1280x720 30 fps，码率上限 8000 kbps/.test(agent.out.slice(videoMark)), 8000)
  check('画质记在这台设备上，下一次远程照样用；只看模式也能换画质（换成自动，被控端重建编码器）', !!remembered && !!viewAuto,
    { remembered: !!remembered, auto: !!viewAuto, menu: await admin.locator('[data-testid=remote-video]').textContent().catch(() => null), agent: encodeLines(agent, viewFrom) })
  check('只看模式下换画质也不会有键鼠到对方电脑', inputs(agent).length === before3, inputs(agent).slice(before3))
  // 没加钩子时这台电脑跟得上测试画面：自动画质在 1280×720 上一直动着跑 6 秒（比"连续 3 秒忙不过来就降"长一倍），
  // 不降分辨率、不标忙、不提示。不然下面"对方电脑忙不过来"那一段什么也证明不了（不加钩子也会降）
  const calm = await watch(6000)
  const calmLog = autoLines(agent, viewFrom)
  const calmOk = !calmLog.some((l) => l.includes('电脑忙不过来'))
    && calm.every((s) => s.size === '1280x720' && (s.flags & 6) === 0 && s.busy === 0)
    // 真的一直在出帧、统计也报上来了（不然上面几样是 0 只是因为没东西可看）
    && calm.some((s) => s.fps >= 5) && calm.at(-1).encode > 0
  const calmCost = calm.map((s) => Math.round((s.capture + s.encode) * 10) / 10)
  check('不加钩子时测试画面不算忙：自动画质动着跑 6 秒，还是 1280×720，没有降分辨率、没有标忙、没有"对方电脑忙不过来"',
    calmOk,
    calmOk ? { perFrameMs: { min: Math.min(...calmCost), max: Math.max(...calmCost) } }
      : { seen: calm, log: calmLog, agent: encodeLines(agent, viewFrom), ...(await badgeNow()) })
  /** 点"结束"，等页面显示已结束。等不到时先把服务器上这次会话的状态、被控端最后几行打出来再抛（光一个超时看不出卡在哪） */
  const endHere = async () => {
    await admin.locator('[data-testid=remote-end]').click()
    try {
      await admin.locator('[data-testid=remote-ended]').waitFor({ timeout: 10000 })
    } catch (e) {
      const row = sql("select state || ',' || coalesce(end_reason, '-') || ',' || coalesce(to_char(ended_at, 'HH24:MI:SS.MS'), '-') from remote_sessions order by started_at desc limit 1")
      console.log('  结束后 10 秒没显示"已结束"：服务器上最近一次会话', row, '；页面上', await admin.locator('[data-testid=remote-view]').getAttribute('data-state').catch(() => '?'),
        '；被控端最后几行', JSON.stringify(agent.out.slice(-700)))
      throw e
    }
  }
  await endHere()

  // ---- 对方电脑忙不过来，两种慢法。每一种都只在这一段重启一次被控端带上测试钩子，别的用例不受它拖累；
  // 后面"对方拒绝"那一段重启被控端时不带钩子 ----
  /** 重启被控端，这一次带上测试钩子 extraEnv；回到默认的自动画质，发起远程控制。返回发起之前被控端输出到了哪 */
  const controlWithHook = async (extraEnv) => {
    agent.proc.kill()
    await until(() => agent.proc.exitCode !== null, 5000)
    agent = startAgent(dir, 'accept', 'pattern', extraEnv)
    await until(() => agent.out.includes('已连接服务器'), 20000)
    await admin.goto(BASE + '/devices')
    // 回到默认的自动（不管前面的用例选过什么）
    await admin.evaluate(() => localStorage.removeItem('remote.video'))
    await card.locator('[data-testid=device-online]', { hasText: '在线' }).waitFor({ timeout: 10000 })
    const from = agent.out.length
    await card.locator('[data-testid=device-control]').click()
    await admin.waitForURL(/\/remote\//, { timeout: 10000 })
    return from
  }
  /** 被控端说的"测试：…"（钩子开没开），检查失败时打出来看 */
  const hookLines = () => agent.out.match(/测试：[^\r\n]*/g) ?? []

  // 一、编码慢：被控端每编一帧故意多睡一会儿（PRIVATECHAT_AGENT_TEST_ENCODE_DELAY_MS=45，按像素数折算：1280×720 睡 45 ms，
  // 960×540 睡 25 ms），像弱电脑上的软件编码，分辨率低了就快。30 帧时一帧只有 33 ms：自动画质要在几秒内把分辨率降一档
  // （测试画面 1280×720 → 960×540），网页上提示对方电脑忙不过来。降了确实省时间，这一档留着、不退回去：
  // 测试画面本身在 Debug 版上 1280×720 每帧约 10 ms（采集 3 + 编码 8，上面"不算忙"那一段量到 9–11 ms），
  // 降之前 10 + 45 ≈ 55 ms（以前跑出来降的那一刻是 60 ms），降之后约 6 + 25 ≈ 31 ms——线程睡得多、电脑降频时
  // 960×540 上实际干活也就 15 ms 左右，合 40 ms——都比降之前的八成五（47–51 ms）少得多
  const cpuMark = await controlWithHook({ PRIVATECHAT_AGENT_TEST_ENCODE_DELAY_MS: '45' })
  const cpuFull = await until(async () => (await canvasSize(admin)) === '1280x720', 20000)
  // 降之前（画面还是 1280×720 时）统计里报的每帧编码耗时：看故意多睡的 45 ms 算没算进编码里
  const cpuBefore = []
  const cpuLog = await until(async () => {
    const p = await perFrame()
    if ((await canvasSize(admin)) === '1280x720') cpuBefore.push(p.encode)
    return agent.out.slice(cpuMark).match(/自动画质：电脑忙不过来[^\r\n]*/)?.[0]
  }, 20000)
  const cpuSmall = await until(async () => (await canvasSize(admin)) === '960x540', 10000)
  const cpuBadge = await until(async () => {
    const p = await perFrame()
    return p.busy > 0 && (p.flags & 6) !== 0 ? p : null
  }, 8000)
  const cpuNow = { ...(await perFrame()), ...(await badgeNow()) }
  check('对方电脑编码跟不上：自动画质把分辨率从 1280×720 降到 960×540，被控端说明原因，网页提示对方电脑忙不过来',
    !!cpuFull && !!cpuLog && !!cpuSmall && !!cpuBadge,
    { started1280: !!cpuFull, log: cpuLog, size: await canvasSize(admin), ...cpuNow, agent: encodeLines(agent, cpuMark), hooks: hookLines() })
  // 降了管用：之后 6 秒（被控端在新的一档上量 2 秒就有结论，没用的话那时就退回去了）一直是 960×540，
  // 统计里标着"因为电脑忙降了分辨率"（头 1.5 秒里可能还是降之前那一秒的统计，不看），被控端没说"降了也没快多少"
  const cpuKept = await watch(6000)
  const cpuAuto = autoLines(agent, cpuMark)
  const keptOk = !!cpuSmall && cpuKept.every((s) => s.size === '960x540') && cpuKept.filter((s) => s.at >= 1500).every((s) => (s.flags & 2) !== 0)
    && !cpuAuto.some((l) => l.includes('降了分辨率也没快多少'))
  check('编码慢时降一档确实省了时间：之后 6 秒还是 960×540（标着因为电脑忙降了分辨率），没有因为"降了也没快多少"退回去', keptOk,
    keptOk ? undefined : { seen: cpuKept, log: cpuAuto, agent: encodeLines(agent, cpuMark), hooks: hookLines() })
  // 故意多睡的算在编码里，而且按像素数折算：1280×720 时 45 ms，960×540 时 25 ms（这会儿统计里已经全是 960×540 的帧）
  const cpuAfter = await perFrame()
  const encodeAt1280 = Math.max(0, ...cpuBefore)
  check('统计里报的每帧编码耗时算上了故意多睡的时间（1280×720 时 45 ms，960×540 时按像素数折成 25 ms）',
    encodeAt1280 >= 40 && cpuAfter.encode >= 24, { at1280: encodeAt1280, at960: cpuAfter.encode, capture: cpuAfter.capture })
  check('降了分辨率之后画面照样在出', (await until(async () => (await shownFps(admin)) >= 3, 8000)) !== null, { fps: await shownFps(admin) })
  await endHere()

  // 二、截屏慢：被控端每采一帧故意多睡 90 ms（PRIVATECHAT_AGENT_TEST_CAPTURE_DELAY_MS），分辨率多大都一样——像真实电脑上
  // GDI 截屏慢（按原尺寸截，缩小还要多费事），降分辨率不省时间。自动画质事先不知道慢在哪，照样先降一档；在 960×540 上量 2 秒，
  // 每帧还是一百来 ms，降一档省下的几毫秒远不到 15%，就退回 1280×720，这次远程里不再因为电脑忙降。
  // 不用 45 ms：线程睡得多时这台电脑上图案本身的开销在两种尺寸下差不多，"省了几成"落在 15% 上下，量出来的结果时有时无（28 次里 7 次没退回）
  // 网页上照样提示对方电脑忙不过来（标着忙、没标"降了分辨率"），鼠标停上去看得到慢在采集上
  const capMark = await controlWithHook({ PRIVATECHAT_AGENT_TEST_CAPTURE_DELAY_MS: '90' })
  const capFull = await until(async () => (await canvasSize(admin)) === '1280x720', 20000)
  const capDown = await until(() => agent.out.slice(capMark).match(/自动画质：电脑忙不过来[^\r\n]*/)?.[0], 20000)
  const capUndo = capDown ? await until(() => agent.out.slice(capMark).match(/自动画质：降了分辨率也没快多少[^\r\n]*/)?.[0], 15000) : null
  // 退回去了：被控端最后一次重建编码器是 1280×720，网页上的画面也回到 1280×720
  const capBack = capUndo
    ? await until(async () => (encodeLines(agent, capMark).at(-1) ?? '').startsWith('编码 1280x720') && (await canvasSize(admin)) === '1280x720', 10000)
    : null
  // 退回之后不再因为电脑忙降：之后 6 秒（比"连续 3 秒忙不过来就降"长一倍）一直是 1280×720
  const capKept = await watch(6000)
  const capAuto = autoLines(agent, capMark)
  const capEncode = encodeLines(agent, capMark)
  const capLast = capEncode.at(-1) ?? ''
  const capNow = { ...(await perFrame()), ...(await badgeNow()) }
  const capSeen = { log: capAuto, agent: capEncode, seen: capKept, now: capNow, hooks: hookLines() }
  const downAt = capAuto.findIndex((l) => l.includes('电脑忙不过来'))
  const undoAt = capAuto.findIndex((l) => l.includes('降了分辨率也没快多少'))
  const capStepped = !!capFull && !!capDown && !!capUndo && downAt >= 0 && undoAt > downAt && capUndo.includes('回到 1280x720')
    && capEncode.some((l) => l.startsWith('编码 960x540')) && !!capBack
    && /^编码 1280x720 30 fps，码率上限 8000 kbps/.test(capLast) && !capLast.includes('降了分辨率')
  check('对方电脑截屏慢：自动画质照样先降到 960×540，量了发现每帧没快多少，退回 1280×720，被控端说明多半慢在截屏上',
    capStepped, capStepped ? { down: capDown, undo: capUndo } : capSeen)
  const capStayed = capKept.length > 0 && capKept.every((s) => s.size === '1280x720') && capAuto.filter((l) => l.includes('电脑忙不过来')).length === 1
  check('退回之后这次远程里不再因为电脑忙降分辨率：之后 6 秒一直是 1280×720', capStayed, capStayed ? undefined : capSeen)
  const capBusy = capNow.busy > 0 && (capNow.flags & 4) !== 0 && (capNow.flags & 2) === 0 && capNow.badge === '对方电脑忙不过来'
  check('网页上照样提示对方电脑忙不过来（统计里标着忙，没标"因为电脑忙降了分辨率"）', capBusy,
    capBusy ? { flags: capNow.flags, title: capNow.badgeTitle } : capSeen)
  check('统计里报的每帧采集耗时算上了故意多睡的 90 ms（不算在编码里）', capNow.capture >= 85 && capNow.encode < 40,
    { capture: capNow.capture, encode: capNow.encode, title: capNow.title })
  await endHere()

  // ---- 对方拒绝 ----
  agent.proc.kill()
  await until(() => agent.proc.exitCode !== null, 5000)
  agent = startAgent(dir, 'decline')
  await until(() => agent.out.includes('已连接服务器'), 20000)
  await admin.goto(BASE + '/devices')
  await card.locator('[data-testid=device-online]', { hasText: '在线' }).waitFor({ timeout: 10000 })
  await card.locator('[data-testid=device-control]').click()
  check('对方拒绝 → 显示"对方拒绝了"', await admin.locator('[data-testid=remote-end-reason]', { hasText: '对方拒绝了' }).waitFor({ timeout: 15000 }).then(() => true, () => false))
  check('Agent 重启后用保存的令牌直接上线（没有再要设备码）', !agent.out.includes('DEVICE-CODE'))

  // ---- 免同意名单：Agent 还是"一律拒绝"，名单里的人点了直接连上 ----
  const openSettings = async (page, c) => {
    await c.locator('[data-testid=device-more]').click()
    await page.locator('[data-testid=device-settings]:visible').click()
    await page.locator('[data-testid=device-trusted]').waitFor()
  }
  await admin.goto(BASE + '/devices')
  await card.locator('[data-testid=device-online]', { hasText: '在线' }).waitFor({ timeout: 10000 })
  await openSettings(admin, card)
  const trustOptions = (await admin.locator('[data-testid=device-trusted] .el-checkbox').allTextContents()).map((s) => s.trim())
  check('电脑设置里能勾免同意的人：主人和远程名单里的人，默认都没勾',
    trustOptions.includes('爸爸（电脑的主人）') && trustOptions.includes('admin') && (await admin.locator('[data-testid=device-trusted] .el-checkbox.is-checked').count()) === 0, trustOptions)
  await admin.locator('[data-testid=device-trusted] .el-checkbox', { hasText: 'admin' }).click()
  await admin.locator('[data-testid=save-device]').click()
  const mine = await card.locator('[data-testid=device-trusted]').waitFor({ timeout: 10000 }).then(() => true, () => false)
  const listed = await card.locator('[data-testid=device-trusted-list]').textContent().catch(() => '')
  const ownerSees = await bobCard.locator('[data-testid=device-trusted-list]', { hasText: 'admin' }).waitFor({ timeout: 10000 }).then(() => true, () => false)
  check('卡片上写明谁免同意：我自己看到提示，主人不用刷新也看得到', mine && listed.includes('免同意：我') && ownerSees, { mine, listed, ownerSees })

  // 主人的提醒几秒后自己消失：先等着，再点
  const ownerNotified = bob.locator('.el-notification', { hasText: '正在控制你的电脑' }).last().waitFor({ timeout: 20000 }).then(() => true, () => false)
  await card.locator('[data-testid=device-control]').click()
  await admin.waitForURL(/\/remote\//, { timeout: 10000 })
  const trustedSession = admin.url().split('/remote/')[1]
  const direct = await until(async () => (await admin.locator('[data-testid=remote-stats]').textContent().catch(() => '')).includes('1280×720'), 20000)
  check('免同意名单里的人：电脑前没人同意（Agent 设成一律拒绝）也直接连上',
    !!direct && sql(`select consent || ',' || state from remote_sessions where id = '${trustedSession}'`) === 'Trusted,Active',
    await admin.locator('[data-testid=remote-end-reason]').textContent().catch(() => ''))
  const banner = !!(await until(() => agent.out.includes('红条：正在被 admin 远程控制'), 5000))
  const notified = await ownerNotified
  check('被控电脑照样显示红条，主人照样收到提醒', banner && notified, { banner, notified })

  // 主人把 admin 从免同意名单里去掉：正在进行的这次立即结束，之后又要等同意（被拒绝）
  await openSettings(bob, bobCard)
  await bob.locator('[data-testid=device-trusted] .el-checkbox', { hasText: 'admin' }).click()
  await bob.locator('[data-testid=save-device]').click()
  check('被移出免同意名单：正在进行的这次远程立即结束', await admin.locator('[data-testid=remote-ended]').waitFor({ timeout: 10000 }).then(() => true, () => false))
  await admin.goto(BASE + '/devices')
  const historyRow = await admin.locator('[data-testid=remote-history-item]').first().textContent({ timeout: 10000 }).catch(() => '')
  check('远程记录里标着这次是"免同意"进来的', historyRow.includes('免同意'), historyRow)
  await card.locator('[data-testid=device-online]', { hasText: '在线' }).waitFor({ timeout: 10000 })
  await card.locator('[data-testid=device-control]').click()
  check('移出名单之后再点：回到等对方同意（这里被拒绝）', await admin.locator('[data-testid=remote-end-reason]', { hasText: '对方拒绝了' }).waitFor({ timeout: 15000 }).then(() => true, () => false))

  // ---- 同意窗口没人点（Agent 设成一直挂着）：误点后取消、改用 PIN，对方电脑上的窗口要马上跟着收起 ----
  agent.proc.kill()
  await until(() => agent.proc.exitCode !== null, 5000)
  agent = startAgent(dir, 'wait')
  await until(() => agent.out.includes('已连接服务器'), 20000)
  await admin.goto(BASE + '/devices')
  await card.locator('[data-testid=device-online]', { hasText: '在线' }).waitFor({ timeout: 10000 })
  /** 从现在起，被控端的输出里出现这句话要多久（毫秒）；超时返回 null */
  const agentSays = (from, text, timeout) => {
    const t = Date.now()
    return until(() => (agent.out.slice(from).includes(text) ? Date.now() - t + 1 : null), timeout, 50)
  }

  let mark = agent.out.length
  await card.locator('[data-testid=device-control]').click()
  await admin.locator('[data-testid=remote-pending]').waitFor({ timeout: 10000 })
  const popped = await agentSays(mark, '同意窗口打开', 5000)
  mark = agent.out.length
  await admin.locator('[data-testid=remote-pending] button').click()
  const closedAfter = await agentSays(mark, '同意窗口关闭', 3000)
  check('误点"远程控制"后取消：对方电脑上的同意窗口马上收起，不用等 30 秒', !!popped && !!closedAfter, { closedAfterMs: closedAfter })

  // 设一个 PIN
  await admin.goto(BASE + '/devices')
  await openSettings(admin, card)
  await admin.locator('[data-testid=device-pin]').fill('246810')
  await admin.locator('[data-testid=save-device]').click()
  await admin.locator('[data-testid=device-settings-dialog]').waitFor({ state: 'hidden', timeout: 10000 }).catch(() => undefined)

  // 又误点了一次"查看屏幕"（窗口挂在对方电脑上，没取消），直接在设备列表里用 PIN 连
  mark = agent.out.length
  await admin.evaluate(async (id) => {
    await fetch('/api/remote/sessions', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ targetDeviceId: id, mode: 'View', pin: null }) })
  }, deviceId)
  const poppedAgain = await agentSays(mark, '同意窗口打开', 5000)
  mark = agent.out.length
  const pinAt = Date.now()
  await card.locator('[data-testid=device-more]').click()
  await admin.locator('[data-testid=device-use-pin]:visible').click()
  await admin.locator('.el-message-box__input input').fill('246810')
  await admin.locator('.el-message-box__btns .el-button--primary').click()
  await admin.waitForURL(/\/remote\//, { timeout: 10000 })
  const startedAfter = await agentSays(mark, '远程会话开始', 8000)
  const pinStream = await until(async () => (await admin.locator('[data-testid=remote-stats]').textContent().catch(() => '')).includes('1280×720'), 20000)
  check('同意窗口还挂着时用 PIN 直接连：窗口收起，被控端立刻开始，几秒内出画面',
    !!poppedAgain && agent.out.slice(mark).includes('同意窗口关闭') && !!startedAfter && !!pinStream,
    { startedAfterMs: startedAfter, pictureAfterMs: Date.now() - pinAt })
  await endHere()

  // ---- 移除电脑：Agent 回到设备码登记 ----
  await admin.goto(BASE + '/devices')
  await card.locator('[data-testid=device-more]').click()
  await admin.locator('[data-testid=device-revoke]:visible').click()
  await admin.locator('.el-message-box__btns .el-button--primary').click()
  check('移除后列表里没有了', await card.waitFor({ state: 'detached', timeout: 10000 }).then(() => true, () => false))
  check('Agent 收到吊销，回到设备码登记', !!(await until(() => /设备已被移除[\s\S]*DEVICE-CODE: \d{8}/.test(agent.out), 15000)), agent.out.slice(-300))
  check('令牌在服务器上已作废', sql(`select revoked_at is not null and token_hash is null from devices where id = '${deviceId}'`) === 't')
  agent.proc.kill()

  // ---- 可选：真实屏幕（只检查尺寸和非黑屏，不截图）----
  if (process.env.E2E_REAL_SCREEN === '1') {
    const realDir = dir + '-real'
    agent = startAgent(realDir, 'accept', 'screen')
    const realCode = await until(() => agent.out.match(/DEVICE-CODE: (\d{8})/)?.[1])
    await admin.goto(BASE + '/devices')
    await admin.locator('[data-testid=add-device]').click()
    await admin.locator('[data-testid=device-code]').fill(realCode ?? '')
    await admin.locator('[data-testid=device-found]').waitFor({ timeout: 10000 })
    await admin.locator('[data-testid=confirm-device]').click()
    const realCard = admin.locator('[data-testid=device]', { hasText: 'E2E 测试电脑' })
    await realCard.locator('[data-testid=device-online]', { hasText: '在线' }).waitFor({ timeout: 20000 })
    await realCard.locator('[data-testid=device-control]').click()
    const real = await until(async () => {
      const size = await canvasSize(admin)
      const lit = await admin.evaluate(() => {
        const c = document.querySelector('[data-testid=remote-canvas]')
        if (!c?.width) return 0
        const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data
        let n = 0
        for (let i = 0; i < d.length; i += 4 * 97) if (d[i] + d[i + 1] + d[i + 2] > 30) n++
        return n
      })
      return size !== '0x0' && size !== '300x150' && lit > 100 ? { size, lit } : null
    }, 20000)
    // 截屏、编码器是哪种：看被控端最后一次建编码器那一行（默认 auto 时刚添加的电脑还没探测完，是 GDI + 软件编码器）
    const realPipeline = pipelineOf(encodeLines(agent).at(-1))
    console.log(`  真实屏幕：截屏 ${realPipeline.capture ?? '（没写）'}，编码器 ${realPipeline.encoder ?? '（没写）'}`)
    check('真实屏幕：截屏 + 编码 + 解码出非黑画面', !!real, { ...(real ?? { size: await canvasSize(admin) }), ...realPipeline })
    if (process.env.PRIVATECHAT_AGENT_CAPTURE === 'dxgi') {
      // 强制桌面复制：截屏那一截是"桌面复制（显卡名）"，不是退回去的 GDI（锁着屏时桌面复制被拒，这一条会失败）
      check('PRIVATECHAT_AGENT_CAPTURE=dxgi：真实屏幕用的是桌面复制，没有退回 GDI',
        realPipeline.capture?.startsWith('桌面复制') === true && !agent.out.includes('截屏改用'), realPipeline)
    }
    // 真实的声音：环回采集这台电脑的播放设备（这会儿没有声音在播就采到静音，不发包；这里只确认设备打得开）
    const loopback = await until(() => agent.out.match(/声音采集已打开[^\r\n]*/)?.[0] ?? (agent.out.includes('声音采集出错') ? 'error' : null), 8000)
    check('真实声音：能打开这台电脑的播放设备做环回采集', !!loopback && loopback !== 'error', loopback ?? agent.out.split(/\r?\n/).filter((l) => l.includes('声音')).slice(-2))
    await admin.locator('[data-testid=remote-end]').click()
    await admin.goto(BASE + '/devices')
    await realCard.locator('[data-testid=device-more]').click()
    await admin.locator('[data-testid=device-revoke]:visible').click()
    await admin.locator('.el-message-box__btns .el-button--primary').click()
    agent.proc.kill()
  }

  const apiLog = (range) => execSync(`${COMPOSE} logs api ${range}`, { maxBuffer: 256 * 1024 * 1024 }).toString().split('\n')
  // 假 SFU 被故意停掉之后，api 去关 Cloudflare 那边的轨道会连不上（带堆栈的警告），被控端每次重新发布都是 502：都属于预期。
  // 那之后只看有没有别的错误
  const errors = sfuStoppedAt
    ? [...apiLog(`--since ${startedAt} --until ${sfuStoppedAt}`).filter((l) => /\bERR\b|Unhandled|Exception/.test(l)), ...apiLog(`--since ${sfuStoppedAt}`).filter((l) => /\bERR\b|Unhandled/.test(l) && !/realtime\/publish responded 502/.test(l))]
    : apiLog(`--since ${startedAt}`).filter((l) => /\bERR\b|Unhandled|Exception/.test(l))
  check('api 日志没有错误', errors.length === 0, errors.slice(0, 3))

  await browser.close()
  await restoreApi()
  cleanup()
  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch(async (e) => {
  console.error(e)
  for (const proc of agents) try { proc.kill() } catch { /* 已退出 */ }
  if (REALTIME) try { execSync(`${COMPOSE} up -d --no-deps api`, { stdio: 'ignore' }); await apiHealthy() } catch { /* 还原不了就算了 */ }
  try { void sfu?.close() } catch { /* 已关 */ }
  process.exit(1)
})
