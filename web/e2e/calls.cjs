// 通话端到端：两个真实 Chromium（假摄像头 / 麦克风）对 compose 栈，媒体经本机的假 SFU（e2e/fake-sfu.cjs）真实转发。
// 覆盖：拨打家人 / 来电弹窗 / 接听后双向音视频 / 静音与关摄像头 / 共享屏幕 / 最小化 / 挂断与通话记录 /
// 拒接 / 未接 / 刷新后来电仍在 / 关页面即离开 / 客厅（常驻会议室）/ 媒体链路中断后自动重连。
// 前提：compose 栈已启动且镜像是当前代码（deploy/ 下 docker compose up -d --build），浏览器已安装（npx playwright install chromium）。
// 用法：cd web && node e2e/calls.cjs；真实 Cloudflare：E2E_REAL_CF=1 node e2e/calls.cjs。
// 默认临时用测试凭据重建 api，结束时按 deploy/.env 还原；真实模式使用运行中 api 的配置，不重建容器。
// 两种模式都只对本地 compose 测试库运行，会创建测试账号与通话记录；真实模式会产生 Cloudflare 用量。
const path = require('path')
const { chromium } = require('playwright')
const { execSync, execFileSync } = require('child_process')

const REAL_CF = process.env.E2E_REAL_CF === '1'
const BASE = process.env.E2E_BASE || 'http://localhost:8080'
const SFU_PORT = Number(process.env.E2E_SFU_PORT || 8788)
const COMPOSE_FILE = path.resolve(__dirname, '../../deploy/docker-compose.yml')
const COMPOSE = `docker compose -f "${COMPOSE_FILE}"`

// 检查实际运行的 api 容器，而不是只看 .env（改了 .env 却没重建容器很常见）。
// 不打印环境变量的值；错误只给变量名，避免密钥出现在测试日志里。
function checkRealConfiguration() {
  let env
  try {
    const id = execFileSync('docker', ['compose', '-f', COMPOSE_FILE, 'ps', '-q', 'api'], { encoding: 'utf8', timeout: 15000 }).trim()
    if (!id) throw new Error('api not running')
    const entries = JSON.parse(execFileSync('docker', ['inspect', '--format', '{{json .Config.Env}}', id], { encoding: 'utf8', timeout: 15000 }))
    env = Object.fromEntries(entries.map((s) => { const i = s.indexOf('='); return [s.slice(0, i), s.slice(i + 1)] }))
  } catch {
    throw new Error('无法检查 api 容器配置，请先启动本地 compose 栈。')
  }
  const keys = {
    AppId: 'CF_REALTIME_APP_ID', AppSecret: 'CF_REALTIME_APP_SECRET',
    TurnKeyId: 'CF_TURN_KEY_ID', TurnApiToken: 'CF_TURN_KEY_API_TOKEN',
  }
  const missing = Object.entries(keys).filter(([key]) => !env[`CloudflareRealtime__${key}`]?.trim()).map(([, name]) => name)
  if (missing.length) throw new Error(`真实模式缺少配置：${missing.join('、')}。填好 deploy/.env 后运行 docker compose -f deploy/docker-compose.yml up -d api。`)
  const base = env.CloudflareRealtime__BaseUrl || 'https://rtc.live.cloudflare.com/v1'
  if (base.replace(/\/$/, '') !== 'https://rtc.live.cloudflare.com/v1')
    throw new Error('真实模式要求 Cloudflare 官方地址；请移除 CF_REALTIME_BASE_URL 测试覆盖并重建 api。')
  if (Object.keys(keys).some((key) => env[`CloudflareRealtime__${key}`].startsWith('e2e-')))
    throw new Error('api 仍在使用 e2e 测试凭据，请填入真实凭据并重建 api。')
}
const FAKE_ENV = {
  CF_REALTIME_BASE_URL: `http://host.docker.internal:${SFU_PORT}/v1`,
  CF_REALTIME_APP_ID: 'e2e-app',
  CF_REALTIME_APP_SECRET: 'e2e-secret',
  CF_TURN_KEY_ID: 'e2e-turn',
  CF_TURN_KEY_API_TOKEN: 'e2e-turn-token',
}
const BOB_ID = '01a1b1b0-0000-7000-8000-0000000000b0'

const results = []
const skips = []
function skip(name, reason) {
  skips.push(name)
  console.log(`SKIP  ${name} — ${reason}`)
}
function check(name, ok, detail) {
  results.push({ name, ok: !!ok })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail !== undefined ? '  — ' + JSON.stringify(detail) : ''}`)
}
const sql = (q) => execSync(`${COMPOSE} exec -T postgres psql -U privatechat -d privatechat -At -c "${q}"`).toString().trim()
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

/** 用给定的环境变量重建 api 容器（shell 里的变量优先于 .env），等它恢复健康 */
async function recreateApi(extraEnv) {
  execSync(`${COMPOSE} up -d --no-build api`, { env: { ...process.env, ...extraEnv }, stdio: 'pipe' })
  for (let i = 0; i < 60; i++) {
    try {
      const res = await fetch(BASE + '/healthz')
      if (res.ok) return
    } catch { /* 还没起来 */ }
    await sleep(1000)
  }
  throw new Error('api 没有在 60 秒内恢复')
}

async function login(page, who) {
  await page.goto(BASE + '/')
  await page.locator('input[autocomplete=username]').fill(who)
  await page.locator('input[type=password]').fill('admin123')
  await page.getByRole('button', { name: '登录' }).click()
  await page.locator('input[type=password]').waitFor({ state: 'detached', timeout: 15000 })
}

const overlay = (page) => page.locator('[data-testid=call-overlay]')
const tile = (userId, kind = 'camera') => `[data-testid=call-tile][data-user-id="${userId}"][data-kind=${kind}]`
const waitPhase = (page, phase, timeout = 20000) =>
  page.waitForSelector(`[data-testid=call-overlay][data-phase=${phase}]`, { timeout }).then(() => true).catch(() => false)
const waitGone = (page, selector, timeout = 15000) => page.waitForSelector(selector, { state: 'detached', timeout }).then(() => true).catch(() => false)

/** 这个格子里的视频真的在出画面：有尺寸，而且播放位置在前进 */
async function videoFlowing(page, selector, timeout = 20000) {
  try {
    await page.waitForFunction((sel) => {
      const v = document.querySelector(sel + ' video')
      return !!v && v.videoWidth > 0 && v.readyState >= 2 && !v.paused
    }, selector, { timeout })
    const t1 = await page.evaluate((sel) => document.querySelector(sel + ' video').currentTime, selector)
    await sleep(700)
    const t2 = await page.evaluate((sel) => { const v = document.querySelector(sel + ' video'); return { t: v.currentTime, size: `${v.videoWidth}x${v.videoHeight}` } }, selector)
    return t2.t > t1 ? t2.size : false
  } catch {
    return false
  }
}

/** 对方的声音真的到了：先等订阅完成（audio 元素出现），再量 6 秒内的峰值（假麦克风会周期性"嘀"一声）。-1 = 没等到音频轨道 */
async function audioPeak(page) {
  const ready = await page.waitForFunction(() => { const el = document.querySelector('[data-testid=call-audio]'); return !!el && !!el.srcObject }, null, { timeout: 15000 }).then(() => true).catch(() => false)
  if (!ready) return -1
  return page.evaluate(async () => {
    const el = document.querySelector('[data-testid=call-audio]')
    if (!el || !el.srcObject) return -1
    const ctx = new AudioContext()
    await ctx.resume()
    const analyser = ctx.createAnalyser()
    analyser.fftSize = 2048
    ctx.createMediaStreamSource(el.srcObject).connect(analyser)
    const buf = new Float32Array(analyser.fftSize)
    let peak = 0
    const t0 = performance.now()
    while (performance.now() - t0 < 6000 && peak < 0.02) {
      analyser.getFloatTimeDomainData(buf)
      for (const v of buf) peak = Math.max(peak, Math.abs(v))
      await new Promise((r) => setTimeout(r, 50))
    }
    await ctx.close()
    return Number(peak.toFixed(3))
  })
}

const toast = (page, text, timeout = 8000) => page.locator('.el-message', { hasText: text }).first().waitFor({ timeout }).then(() => true).catch(() => false)

;(async () => {
  let sfu, browser
  let restoreApi = false
  // Ctrl+C / 被终止时 finally 不会执行：也要把 api 从假 SFU 改回 deploy/.env 的配置，不能把本地栈留在测试凭据上
  let interrupted = false
  const onSignal = (signal) => {
    if (interrupted) return
    interrupted = true
    console.log(`\n收到 ${signal}，还原 api 配置后退出…`)
    try { sfu?.close?.() } catch { /* ignore */ }
    if (restoreApi) {
      try {
        execSync(`${COMPOSE} up -d --no-build api`, { stdio: 'inherit' })
      } catch {
        console.log('还原失败，请运行 docker compose -f deploy/docker-compose.yml up -d api')
      }
    }
    process.exit(130)
  }
  // Windows：Ctrl+C 是 SIGINT，Ctrl+Break 是 SIGBREAK，关掉终端窗口是 SIGHUP（SIGTERM 在 Windows 上不会来）
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK']) process.on(signal, onSignal)
  try {
    if (!['', '0', '1'].includes(process.env.E2E_REAL_CF || '')) throw new Error('E2E_REAL_CF 只接受 0 或 1；拒绝把拼错的真实模式当成假 SFU 模式。')
    const target = new URL(BASE)
    if (target.protocol !== 'http:' || !['localhost', '127.0.0.1', '[::1]'].includes(target.hostname) || target.username || target.password || target.pathname !== '/' || target.search || target.hash)
      throw new Error('此脚本只用于本地 compose 测试库，E2E_BASE 必须是 http://localhost:端口（或回环 IP），不要指向线上家庭应用。')
    console.log(REAL_CF ? 'MODE 真实 Cloudflare：不启动假 SFU、不重建 api；使用合成音视频，会产生 Cloudflare 用量。' : 'MODE 本地假 SFU')
    if (REAL_CF) checkRealConfiguration()

    // 不再全库 UPDATE 强制结束所有通话，避免打断用户正在进行的电话。
    const active = Number(sql('select (select count(*) from call_sessions where ended_at is null and room_slug is null) + (select count(*) from call_participants where left_at is null)'))
    if (!Number.isFinite(active) || active !== 0) throw new Error('有进行中的通话或在场参与者，请先挂断；上次异常退出时等待心跳 / 响铃超时后再运行。')
    const ADMIN_ID = sql(`select id from users where email = 'admin@example.com'`)
    if (!ADMIN_ID) throw new Error('仅支持本地测试账号 admin@example.com / admin123，请勿对生产库运行。')
    const adminName = sql(`select display_name from users where email = 'admin@example.com'`)
    sql(`insert into users (id, email, username, display_name, password_hash, role, elder_mode, created_at) select '${BOB_ID}', 'bob@example.com', 'bob', '爸爸', password_hash, 'Member', false, now() from users where email='admin@example.com' on conflict (id) do nothing`)

    if (!REAL_CF) {
      const { startFakeSfu } = require('./fake-sfu.cjs')
      sfu = await startFakeSfu({ port: SFU_PORT })
      restoreApi = true // 即使重建中途失败，也要在 finally 里尝试还原
      await recreateApi(FAKE_ENV)
    }

    browser = await chromium.launch({
      args: [
        '--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream',
        '--disable-features=WebRtcHideLocalIpsWithMdns', '--allow-loopback-in-peer-connection',
        '--autoplay-policy=no-user-gesture-required',
      ],
    })
    const newUser = async (who, viewport) => {
      const ctx = await browser.newContext({ viewport, locale: 'zh-CN', permissions: ['camera', 'microphone'] })
      // headless 里没有真实屏幕可选：共享屏幕用一块会动的画布代替
      await ctx.addInitScript(() => {
        if (!navigator.mediaDevices) return
        navigator.mediaDevices.getDisplayMedia = async () => {
          const c = document.createElement('canvas')
          c.width = 960; c.height = 540
          const g = c.getContext('2d')
          let n = 0
          setInterval(() => {
            g.fillStyle = `hsl(${(n * 7) % 360} 70% 45%)`; g.fillRect(0, 0, c.width, c.height)
            g.fillStyle = '#fff'; g.font = '64px sans-serif'; g.fillText('SCREEN ' + n++, 60, 280)
          }, 100)
          return c.captureStream(10)
        }
      })
      const page = await ctx.newPage()
      page.on('pageerror', (e) => console.log(`  [pageerror ${who}]`, String(e).slice(0, 200)))
      await login(page, who)
      return { ctx, page }
    }
    const admin = await newUser('admin@example.com', { width: 1280, height: 860 })
    const bob = await newUser('bob@example.com', { width: 420, height: 860 })
    const a = admin.page
    let b = bob.page

    // ---------- 通话页：客厅、家人列表 ----------
    await a.goto(BASE + '/calls')
    await a.waitForSelector('[data-testid=living-room]', { timeout: 15000 })
    const config = await a.evaluate(() => fetch('/api/calls/config', { credentials: 'include' }).then((r) => r.json()))
    check('通话功能已启用', config.enabled === true, config)
    const memberRow = a.locator(`[data-testid=family-member][data-user-id="${BOB_ID}"]`)
    await memberRow.waitFor({ timeout: 10000 })
    check('通话页列出家人，可以直接拨打', (await memberRow.innerText()).includes('爸爸'))
    check('没人时客厅显示为空', (await a.locator('[data-testid=living-room-people]').innerText()).includes('没有人'))

    // ---------- 1. 视频通话：拨打 → 来电 → 接听 → 双向音视频 ----------
    await b.goto(BASE + '/messages')
    await memberRow.locator('[data-testid=dial-video]').click()
    check('拨出后进入"正在呼叫"', await waitPhase(a, 'outgoing'))
    const ring = await b.waitForSelector('[data-testid=incoming-call]', { timeout: 15000 }).then(() => true).catch(() => false)
    check('对方在任意页面弹出来电窗', ring)
    check('来电窗显示来电人和类型', ring && (await b.locator('[data-testid=incoming-call]').innerText()).includes(adminName) && (await b.locator('[data-testid=incoming-call]').innerText()).includes('视频通话'))
    await b.click('[data-testid=call-accept]')
    check('接听后双方都进入通话中', (await waitPhase(a, 'active')) && (await waitPhase(b, 'active')))
    check('来电窗在接听后消失', await waitGone(b, '[data-testid=incoming-call]'))
    const aSeesB = await videoFlowing(a, tile(BOB_ID))
    const bSeesA = await videoFlowing(b, tile(ADMIN_ID))
    check('发起方看到对方的视频画面在动', !!aSeesB, aSeesB)
    check('接听方看到对方的视频画面在动', !!bSeesA, bSeesA)
    const aHears = await audioPeak(a)
    const bHears = await audioPeak(b)
    check('双方都收到对方的声音', aHears > 0.02 && bHears > 0.02, { aHears, bHears })
    check('自己的预览画面显示在角落', !!(await videoFlowing(a, '[data-testid=call-tile][data-kind=self]')))
    const connected = await a.waitForSelector('[data-testid=call-overlay][data-media=connected]', { timeout: 10000 }).then(() => true).catch(() => false)
    check('媒体连接状态为已连接', connected)
    const timerText = await a.locator('[data-testid=call-status]').innerText()
    check('通话界面显示计时', /^\d\d:\d\d/.test(timerText), timerText)
    const convId = sql(`select conversation_id from call_sessions where ended_at is null and room_slug is null`)
    check('数据库里这通通话已接通、两人在场', sql(`select count(*) from call_participants p join call_sessions c on c.id = p.call_id where c.ended_at is null and c.room_slug is null and c.answered_at is not null and p.left_at is null`) === '2')

    // ---------- 2. 静音 / 关摄像头 ----------
    await b.click('[data-testid=call-mic]')
    check('对方静音后，这边的格子上出现静音标记', await a.waitForSelector(`${tile(BOB_ID)} [data-testid=tile-muted]`, { timeout: 8000 }).then(() => true).catch(() => false))
    await b.click('[data-testid=call-mic]')
    check('取消静音后标记消失', await waitGone(a, `${tile(BOB_ID)} [data-testid=tile-muted]`, 8000))
    await b.click('[data-testid=call-cam]')
    check('对方关摄像头后，这边显示头像而不是黑画面', await a.waitForSelector(`${tile(BOB_ID)}[data-video=off]`, { timeout: 8000 }).then(() => true).catch(() => false))
    check('关摄像头的人自己的预览也收起', await waitGone(b, '[data-testid=call-tile][data-kind=self]', 5000))
    const beforeToggle = sfu?.requests.length
    await b.click('[data-testid=call-cam]')
    await a.waitForSelector(`${tile(BOB_ID)}[data-video=on]`, { timeout: 8000 }).catch(() => undefined)
    check('重新打开摄像头后画面恢复', !!(await videoFlowing(a, tile(BOB_ID))))
    if (REAL_CF) skip('开关摄像头只是换源，没有向 SFU 重新协商', '需要假 SFU 的请求日志；真实模式仍检查画面恢复')
    else check('开关摄像头只是换源，没有向 SFU 重新协商', sfu.requests.length === beforeToggle, sfu.requests.slice(beforeToggle))

    // ---------- 3. 共享屏幕 ----------
    await a.click('[data-testid=call-screen]')
    check('共享的人看到"正在共享屏幕"', await a.waitForSelector('[data-testid=call-sharing]', { timeout: 8000 }).then(() => true).catch(() => false))
    const screenSeen = await videoFlowing(b, tile(ADMIN_ID, 'screen'))
    check('对方多出一格共享的屏幕并且在动', !!screenSeen, screenSeen)
    check('共享屏幕时摄像头画面还在', !!(await videoFlowing(b, tile(ADMIN_ID))))
    await a.click('[data-testid=call-screen]')
    check('停止共享后对方的屏幕格子消失', await waitGone(b, tile(ADMIN_ID, 'screen'), 10000))
    check('停止共享后摄像头画面不受影响', !!(await videoFlowing(b, tile(ADMIN_ID))))

    // ---------- 4. 最小化 ----------
    await a.click('[data-testid=call-minimize]')
    check('最小化后变成悬浮条', await a.waitForSelector('[data-testid=call-mini]', { timeout: 5000 }).then(() => true).catch(() => false))

    // 整页跳转等于关页面：浏览器用 sendBeacon 通知离开，单聊里一方离开通话即结束，不用等 35 秒的心跳超时
    await a.goto(BASE + '/messages')
    check('关闭 / 整页刷新页面会立刻离开通话，对方随即收场', await waitGone(b, '[data-testid=call-overlay]', 15000))
    check('对方看到"通话已结束"', await toast(b, '通话已结束', 5000))
    const first = sql(`select end_reason || ' ' || (answered_at is not null) from call_sessions where conversation_id = '${convId}' order by started_at desc limit 1`)
    check('通话记为正常结束', first === 'Completed true', first)

    // ---------- 5. 通话记录：聊天里的记录消息、通话页的最近通话 ----------
    await b.goto(`${BASE}/messages/${convId}`)
    const callMsg = b.locator('[data-testid=call-message]').last()
    await callMsg.waitFor({ timeout: 10000 })
    const msgText = await callMsg.innerText()
    check('聊天里出现通话记录消息（类型 + 时长）', /视频通话 · 时长 \d\d:\d\d/.test(msgText), msgText)
    const titleOk = await b.waitForFunction((n) => document.querySelector('[data-testid=chat-title]')?.textContent?.trim() === n, adminName, { timeout: 8000 }).then(() => true).catch(() => false)
    check('单聊标题显示对方的名字', titleOk, await b.locator('[data-testid=chat-title]').innerText())
    await a.goto(BASE + '/calls')
    await a.waitForSelector('[data-testid=history-item]', { timeout: 10000 })
    const hist = await a.locator('[data-testid=history-item]').first().innerText()
    check('通话页的最近通话里有这一通', hist.includes('爸爸') && /时长 \d\d:\d\d/.test(hist), hist)

    // ---------- 6. 最小化后站内切换页面：通话不断 ----------
    await b.click('[data-testid=chat-call-audio]')
    check('从聊天页发起语音通话', await waitPhase(b, 'outgoing'))
    await a.waitForSelector('[data-testid=incoming-call]', { timeout: 15000 })
    check('语音来电写的是语音通话', (await a.locator('[data-testid=incoming-call]').innerText()).includes('语音通话'))
    await a.click('[data-testid=call-accept]')
    await waitPhase(a, 'active'); await waitPhase(b, 'active')
    const voicePeak = await audioPeak(a)
    check('语音通话接通后有声音、没有视频格子', voicePeak > 0.02 && (await a.locator(`${tile(BOB_ID)}[data-video=off]`).count()) === 1, { voicePeak })
    await a.click('[data-testid=call-minimize]')
    await a.waitForSelector('[data-testid=call-mini]', { timeout: 5000 })
    // 站内路由跳转（不是整页刷新）
    await a.click('a[href="/drive"]')
    await a.waitForURL('**/drive/**')
    const miniPeak = await audioPeak(a)
    check('最小化后切到网盘，悬浮条还在、声音不断', (await a.locator('[data-testid=call-mini]').count()) === 1 && miniPeak > 0.02, { miniPeak })
    // 语音通话里打开摄像头：新发布一条视频轨道
    await a.click('[data-testid=call-mini] button')
    await a.waitForSelector('[data-testid=call-overlay]')
    await a.click('[data-testid=call-cam]')
    const upgraded = await videoFlowing(b, tile(ADMIN_ID))
    check('语音通话中打开摄像头，对方看到画面', !!upgraded, upgraded)
    await b.click('[data-testid=call-hangup]')
    check('一方挂断，双方通话界面都关闭', (await waitGone(b, '[data-testid=call-overlay]')) && (await waitGone(a, '[data-testid=call-overlay]')))

    // ---------- 7. 拒接 ----------
    await a.goto(BASE + '/calls')
    await a.locator(`[data-testid=family-member][data-user-id="${BOB_ID}"] [data-testid=dial-audio]`).click()
    await waitPhase(a, 'outgoing')
    await b.waitForSelector('[data-testid=incoming-call]', { timeout: 15000 })
    await b.click('[data-testid=call-decline]')
    check('拒接后来电窗消失', await waitGone(b, '[data-testid=incoming-call]', 5000))
    check('发起方收场并提示"对方已拒绝"', (await toast(a, '对方已拒绝')) && (await waitGone(a, '[data-testid=call-overlay]')))
    check('拒接记入通话记录', sql(`select end_reason from call_sessions where conversation_id = '${convId}' order by started_at desc limit 1`) === 'Declined')

    // ---------- 8. 未接：发起人在接听前挂断 ----------
    await a.locator(`[data-testid=family-member][data-user-id="${BOB_ID}"] [data-testid=dial-video]`).click()
    await waitPhase(a, 'outgoing')
    await b.waitForSelector('[data-testid=incoming-call]', { timeout: 15000 })
    await a.click('[data-testid=call-hangup]')
    check('对方取消后，来电窗自动消失', await waitGone(b, '[data-testid=incoming-call]', 8000))
    await b.goto(`${BASE}/messages/${convId}`)
    // 来电窗随"通话结束"事件收起，通话记录消息紧接着才写：等最后一条变成这一通的记录，而不是读到上一通
    await b.waitForFunction(() => [...document.querySelectorAll('[data-testid=call-message]')].pop()?.textContent?.includes('未接来电'), null, { timeout: 10000 }).catch(() => undefined)
    const missedText = await b.locator('[data-testid=call-message]').last().innerText()
    check('没接到的人在聊天里看到"未接来电"', missedText.includes('未接来电'), missedText)
    await a.goto(`${BASE}/messages/${convId}`)
    await a.waitForFunction(() => [...document.querySelectorAll('[data-testid=call-message]')].pop()?.textContent?.includes('已取消'), null, { timeout: 10000 }).catch(() => undefined)
    const cancelledText = await a.locator('[data-testid=call-message]').last().innerText()
    check('发起人看到的是"已取消"', cancelledText.includes('已取消'), cancelledText)

    // ---------- 9. 响铃中刷新页面：来电窗还在；会话里显示"加入" ----------
    await a.click('[data-testid=chat-call-video]')
    await waitPhase(a, 'outgoing')
    await b.waitForSelector('[data-testid=incoming-call]', { timeout: 15000 })
    await b.reload()
    check('响铃中刷新页面，来电窗重新弹出', await b.waitForSelector('[data-testid=incoming-call]', { timeout: 15000 }).then(() => true).catch(() => false))
    check('聊天页同时显示"通话进行中，加入"', await b.waitForSelector('[data-testid=chat-call-banner]', { timeout: 8000 }).then(() => true).catch(() => false))
    // 来电窗是模态的，盖在"加入"按钮上面：这里直接派发点击来走"加入"这条路（响铃超时、静音会话时用户走的就是它）
    await b.locator('[data-testid=chat-call-join]').dispatchEvent('click')
    check('点"加入"也能接通', (await waitPhase(b, 'active')) && (await waitPhase(a, 'active')) && !!(await videoFlowing(b, tile(ADMIN_ID))))
    await a.click('[data-testid=call-hangup]')
    await waitGone(b, '[data-testid=call-overlay]')

    // ---------- 10. 客厅（常驻会议室） ----------
    await a.goto(BASE + '/calls')
    await b.goto(BASE + '/calls')
    await b.waitForSelector('[data-testid=living-room]')
    await a.click('[data-testid=living-room-join]')
    check('进客厅不需要等人接听，直接是通话中', await waitPhase(a, 'active'))
    check('通话界面标题是"客厅"', (await a.locator('[data-testid=call-title]').innerText()).trim() === '客厅')
    const live = await b.waitForFunction((n) => document.querySelector('[data-testid=living-room-people]')?.textContent?.includes(n), adminName, { timeout: 8000 }).then(() => true).catch(() => false)
    check('没进去的家人实时看到谁在客厅里', live)
    check('客厅不响铃', (await b.locator('[data-testid=incoming-call]').count()) === 0)
    await b.click('[data-testid=living-room-join]')
    await waitPhase(b, 'active')
    check('两人在客厅里互相看到画面、听到声音', !!(await videoFlowing(a, tile(BOB_ID))) && !!(await videoFlowing(b, tile(ADMIN_ID))) && (await audioPeak(b)) > 0.02)

    // ---------- 11. 媒体链路中断 → 自动重连 ----------
    if (REAL_CF) {
      for (const name of [
        '服务端这条连接断掉后，客户端发现并开始重连',
        '重连后回到通话中',
        '重连换了新的 Cloudflare 会话',
        '重连后对方重新看到画面、听到声音',
        '重连的一方也重新看到对方',
      ]) skip(name, '需要假 SFU 主动断开媒体连接；真实网络切换须另行实测')
    } else {
      const adminSession = sql(`select p.cf_session_id from call_participants p join call_sessions c on c.id = p.call_id where c.room_slug is not null and p.left_at is null and p.user_id = '${ADMIN_ID}'`)
      const dropped = await sfu.drop(adminSession)
      // ICE 要过几秒才发现对端没了（consent 超时），再加 5 秒宽限才重连；重连本身很快，看"重新加入"的日志而不是抓那一瞬间的状态
      const reconnectLogged = await new Promise((resolve) => {
        const timer = setTimeout(() => resolve(false), 40000)
        a.on('console', function onMsg(m) { if (m.text().includes('重新加入')) { clearTimeout(timer); a.off('console', onMsg); resolve(true) } })
      })
      check('服务端这条连接断掉后，客户端发现并开始重连', dropped && reconnectLogged)
      check('重连后回到通话中', await waitPhase(a, 'active', 30000) && (await a.locator('[data-testid=call-overlay][data-media=connected]').waitFor({ timeout: 20000 }).then(() => true).catch(() => false)))
      const newSession = sql(`select p.cf_session_id from call_participants p join call_sessions c on c.id = p.call_id where c.room_slug is not null and p.left_at is null and p.user_id = '${ADMIN_ID}'`)
      check('重连换了新的 Cloudflare 会话', !!newSession && newSession !== adminSession, { before: adminSession.slice(0, 12), after: newSession.slice(0, 12) })
      check('重连后对方重新看到画面、听到声音', !!(await videoFlowing(b, tile(ADMIN_ID), 30000)) && (await audioPeak(b)) > 0.02)
      check('重连的一方也重新看到对方', !!(await videoFlowing(a, tile(BOB_ID), 30000)))
    }

    // ---------- 12. 离开客厅：房间还在 ----------
    await b.click('[data-testid=call-hangup]')
    await waitGone(b, '[data-testid=call-overlay]')
    check('有人离开后，留下的人还在客厅里，对方的格子消失', (await waitGone(a, tile(BOB_ID), 10000)) && (await overlay(a).count()) === 1)
    await a.click('[data-testid=call-hangup]')
    await waitGone(a, '[data-testid=call-overlay]')
    // 界面在收到服务器确认之前就关了（挂断先本地收场）：离开请求可能还在路上，轮询几秒等服务器记完
    const roomSql = `select (ended_at is null) || ' ' || (select count(*) from call_participants p where p.call_id = c.id and p.left_at is null) from call_sessions c where room_slug = 'living-room'`
    let room = sql(roomSql)
    for (let i = 0; i < 20 && room !== 'true 0'; i++) {
      await sleep(250)
      room = sql(roomSql)
    }
    check('所有人离开后客厅仍然存在、里面没有人', room === 'true 0', room)
    check('客厅不产生通话记录消息', sql(`select count(*) from messages m join call_sessions c on c.id = m.client_message_id where c.room_slug is not null`) === '0')

    // ---------- 13. 信令没有违反"同一会话串行"的约束 ----------
    if (REAL_CF) skip('整个过程中假 SFU 没有收到乱序 / 非法的请求', '真实 Cloudflare 不提供假 SFU 的请求记录')
    else {
      const protocolErrors = sfu.errors.filter((e) => e.status === 406 || e.status === 400 || e.status === 401 || e.status >= 500)
      check('整个过程中假 SFU 没有收到乱序 / 非法的请求', protocolErrors.length === 0, protocolErrors.slice(0, 3))
      const ops = sfu.requests.reduce((m, r) => { const k = r.path.replace(/sessions\/[^/]+/, 'sessions/*').replace(/^\/v1\/apps\/[^/]+/, ''); m[k] = (m[k] || 0) + 1; return m }, {})
      console.log('  SFU 请求统计：', JSON.stringify(ops))
    }
    const apiLines = execSync(`${COMPOSE} logs --since 10m --no-log-prefix api`, { maxBuffer: 256 * 1024 * 1024 }).toString().split('\n')
    const apiErrors = []
    apiLines.forEach((l, i) => { if (/\[\d\d:\d\d:\d\d ERR\]/.test(l)) apiErrors.push([l, ...apiLines.slice(i + 1, i + 12).filter((x) => /Exception|^\s+at /.test(x))].join(' | ').slice(0, 600)) })
    check('api 日志里没有错误', apiErrors.length === 0, apiErrors.slice(0, 3))
  } catch (e) {
    check('脚本执行中断', false, String(e && e.stack ? e.stack : e).slice(0, 600))
  } finally {
    if (browser) await browser.close().catch(() => undefined)
    if (sfu) await sfu.close().catch((e) => check('关闭假 SFU', false, String(e)))
    // 只有假模式改过容器配置才还原；真实模式不能重建 api 或改掉用户的凭据。
    if (restoreApi) await recreateApi({}).catch(() => check('还原 api 配置', false, '还原失败，请运行 docker compose -f deploy/docker-compose.yml up -d api'))
  }

  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} 通过，${failed.length} 失败，${skips.length} 跳过（${REAL_CF ? '真实 Cloudflare' : '本地假 SFU'}）`)
  process.exit(failed.length ? 1 : 0)
})()
