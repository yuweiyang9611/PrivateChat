// 弱网下的远程控制：被控端经一条限速 + 加延迟的线路（e2e/slow-link.cjs）连服务器，量真实的端到端延迟和帧率。
// 被控端用"忙碌的测试画面"（--source busy）：画面里有一块滚 4 秒、停 4 秒的文字（滚动时每帧几十 KB），
// 左上角一排黑白方块是采集时刻的时间码。网页把画面解出来之后读回时间码，和自己的时钟一比就是端到端延迟
// （两边在同一台电脑上，同一个时钟）；不同的时间码一秒出现几个就是真正看到的帧率。
// 三种线路：300 kbps、1 Mbps（都是单程 50 ms），以及带宽够但延迟高的（8 Mbps、单程 150 ms）。
// 前提：compose 栈已启动，装有 Edge。用法：cd web && node e2e/remote-weak.cjs
// E2E_AGENT_EXE=别的 PrivateChat.Agent.exe：拿另一个版本的被控端跑同样的线路，只打印数字不判断（对比用，
// 那个版本也得认识 --source busy）。
const fs = require('fs')
const os = require('os')
const path = require('path')
const { chromium } = require('playwright')
const { execSync, spawn } = require('child_process')
const slowLink = require('./slow-link.cjs')

const BASE = process.env.E2E_BASE || 'http://localhost:8080'
const ROOT = path.resolve(__dirname, '../..')
const OWN_AGENT = path.resolve(ROOT, 'src/PrivateChat.Agent/bin/Debug/net11.0-windows/PrivateChat.Agent.exe')
const AGENT = process.env.E2E_AGENT_EXE || OWN_AGENT
const COMPARE_ONLY = !!process.env.E2E_AGENT_EXE
const results = []
function check(name, ok, detail) {
  if (COMPARE_ONLY) {
    console.log(`INFO  ${name}${detail !== undefined ? '  — ' + JSON.stringify(detail) : ''}`)
    return
  }
  results.push({ name, ok: !!ok })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail !== undefined ? '  — ' + JSON.stringify(detail) : ''}`)
}
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

/** 在页面里每 10 ms 左右读一次时间码，收集 seconds 秒：每个新时间码记下它是什么时候显示出来的、延迟多少 */
const sample = (page, seconds) => page.evaluate(async (seconds) => {
  const canvas = document.querySelector('[data-testid=remote-canvas]')
  const ctx = canvas.getContext('2d')
  const BITS = 20
  const MASK = (1 << BITS) - 1
  const read = () => {
    if (!canvas.width) return null
    // 一次读回整行（每次 getImageData 都要从显卡读回，一格一格读的话一秒读不了几次）
    const row = ctx.getImageData(0, Math.floor(24 / 720 * canvas.height), canvas.width, 1).data
    let bits = 0
    for (let i = 0; i < BITS + 4; i++) {
      const o = Math.floor((8 + i * 36 + 16) / 1280 * canvas.width) * 4
      if (row[o] + row[o + 1] + row[o + 2] > 384) bits |= 1 << i
    }
    const stamp = bits & MASK
    let sum = 0
    for (let i = 0; i < BITS; i += 4) sum += (stamp >> i) & 0xf
    return (sum & 0xf) === (bits >>> BITS) ? stamp : null // 校验不过：这一帧糊得读不出来，或者正好画到一半
  }
  const frames = []
  let last = -1
  let unreadable = 0
  const until = performance.now() + seconds * 1000
  while (performance.now() < until) {
    const stamp = read()
    if (stamp === null) unreadable++
    else if (stamp !== last) {
      last = stamp
      const now = Date.now()
      const latency = (((Math.floor(now / 10) & MASK) - stamp) & MASK) * 10
      if (latency < 120_000) frames.push({ at: now, latency })
    }
    await new Promise((r) => setTimeout(r, 10))
  }
  return { frames, unreadable }
}, seconds)

function summarize({ frames, unreadable }, seconds) {
  if (frames.length === 0) return { frames: 0, unreadable }
  const sorted = frames.map((f) => f.latency).sort((a, b) => a - b)
  const at = (p) => sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * p))]
  let longestFreeze = 0
  for (let i = 1; i < frames.length; i++) longestFreeze = Math.max(longestFreeze, frames[i].at - frames[i - 1].at)
  return { fps: +(frames.length / seconds).toFixed(1), median: at(0.5), p90: at(0.9), max: sorted[sorted.length - 1], longestFreeze, unreadable }
}

const dirs = []
let agent = null
let link = null
// 只收拾自己起的被控端和线路（别的测试可能也开着被控端，不能按进程名一锅端）；数据目录里有设备令牌，测完删掉
function cleanup() {
  try { agent?.kill() } catch { /* 已退出 */ }
  try { link?.close() } catch { /* 已关 */ }
  for (const d of dirs) try { fs.rmSync(d, { recursive: true, force: true }) } catch { /* 进程还占着 */ }
}

;(async () => {
  const stamp = Date.now()
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK']) process.on(signal, () => { cleanup(); process.exit(1) })

  if (!COMPARE_ONLY) execSync(`dotnet build "${path.resolve(ROOT, 'src/PrivateChat.Agent')}" -nologo -v q`, { stdio: 'inherit' })
  console.log(`被控端：${AGENT}`)
  const browser = await chromium.launch({ channel: 'msedge' })
  const page = await (await browser.newContext({ viewport: { width: 1280, height: 900 }, locale: 'zh-CN' })).newPage()
  page.on('pageerror', (e) => console.log('  [pageerror]', String(e).slice(0, 200)))
  await page.goto(BASE + '/')
  await page.locator('input[autocomplete=username]').fill('admin@example.com')
  await page.locator('input[type=password]').fill('admin123')
  await page.getByRole('button', { name: '登录' }).click()
  await page.locator('input[type=password]').waitFor({ state: 'detached', timeout: 15000 })

  /** 一种线路跑一遍：新起一个被控端经这条线路连上来，添加、远程、量 40 秒（5 轮"滚 4 秒、停 4 秒"），结束、移除 */
  async function scenario(label, options, expect) {
    // E2E_ONLY=300：只跑名字里带这几个字的线路
    if (process.env.E2E_ONLY && !label.includes(process.env.E2E_ONLY)) return null
    const name = `弱网测试 ${label}`
    const dir = path.join(os.tmpdir(), `pc-agent-weak-${stamp}-${dirs.length}`)
    dirs.push(dir)
    fs.mkdirSync(dir, { recursive: true })
    link = await slowLink.start(BASE, options)
    let out = ''
    agent = spawn(AGENT, ['--headless', '--server', link.url, '--data-dir', dir, '--name', name, '--consent', 'accept', '--source', 'busy', '--dry-input', path.join(dir, 'input.jsonl')],
      { env: { ...process.env, PRIVATECHAT_AGENT_TEST: '1' } })
    agent.stdout.on('data', (d) => { out += d.toString() })
    agent.stderr.on('data', (d) => { out += d.toString() })
    const code = await until(() => out.match(/DEVICE-CODE: (\d{8})/)?.[1], 30000)
    if (!code) throw new Error(`${label}：被控端没有申请到设备码\n${out.slice(-500)}`)

    await page.goto(BASE + '/devices')
    await page.locator('[data-testid=add-device]').click()
    await page.locator('[data-testid=device-code]').fill(code)
    await page.locator('[data-testid=device-found]').waitFor({ timeout: 15000 })
    await page.locator('[data-testid=confirm-device]').click()
    const card = page.locator('[data-testid=device]', { hasText: name })
    await card.locator('[data-testid=device-online]', { hasText: '在线' }).waitFor({ timeout: 30000 })
    await card.locator('[data-testid=device-control]').click()
    await page.waitForURL(/\/remote\//, { timeout: 15000 })
    const started = Date.now()
    const first = await until(async () => (await sample(page, 0.2)).frames.length > 0, 60000, 100)
    const firstPicture = Date.now() - started
    if (!first) throw new Error(`${label}：一分钟都没看到画面\n${out.slice(-800)}`)

    // 前 8 秒让码率估计稳定下来，不算；之后量 40 秒
    await sleep(8000)
    const upBefore = link.bytes.up
    const seconds = 40
    const measured = summarize(await sample(page, seconds), seconds)
    const kbps = Math.round((link.bytes.up - upBefore) * 8 / 1000 / seconds)
    const statsText = await page.locator('[data-testid=remote-stats]').textContent().catch(() => '')
    const slowChip = await page.locator('[data-testid=remote-slow]').count()
    const size = await page.evaluate(() => { const c = document.querySelector('[data-testid=remote-canvas]'); return `${c.width}x${c.height}` })
    console.log(`\n== ${label} ==  第一帧画面 ${firstPicture} ms；工具栏：${statsText}；画面 ${size}；被控端实际发出 ${kbps} kbps`)
    console.log(`   看到的帧率 ${measured.fps} 帧/秒；端到端延迟 中位 ${measured.median} ms / 九成以内 ${measured.p90} ms / 最大 ${measured.max} ms；最长一次画面不动 ${measured.longestFreeze} ms`)
    // 被控端日志里和网络、编码、连接有关的几行（E2E_VERBOSE=1 多打一些）
    const lines = out.split(/\r?\n/).filter((l) => /网络：|编码 \d|控制端|数据通道|出错/.test(l))
    for (const line of lines.slice(-(process.env.E2E_VERBOSE ? 60 : 8))) console.log('   ' + line.replace(/^\S+ \S+ \[\w+\] /, ''))

    check(`${label}：画面一直在动（没有长时间卡住）`, measured.frames !== 0 && measured.longestFreeze <= expect.freeze, { longestFreeze: measured.longestFreeze, limit: expect.freeze })
    check(`${label}：端到端延迟九成在 ${expect.p90} ms 以内`, measured.p90 <= expect.p90, { median: measured.median, p90: measured.p90, max: measured.max })
    check(`${label}：看到的帧率不低于 ${expect.fps} 帧/秒`, measured.fps >= expect.fps, measured.fps)
    if (expect.kbps) check(`${label}：实际发出的码率没有超过线路（${options.kbps} kbps）`, kbps <= expect.kbps, kbps)
    if (expect.slow !== undefined) check(`${label}：工具栏${expect.slow ? '提示' : '不提示'}"网络较慢"`, (slowChip > 0) === expect.slow, { statsText, slowChip })

    await page.locator('[data-testid=remote-end]').click()
    await page.locator('[data-testid=remote-ended]').waitFor({ timeout: 15000 })
    await page.goto(BASE + '/devices')
    await card.locator('[data-testid=device-more]').click()
    await page.locator('[data-testid=device-revoke]:visible').click()
    await page.locator('.el-message-box__btns .el-button--primary').click()
    await card.waitFor({ state: 'detached', timeout: 15000 })
    agent.kill()
    await until(() => agent.exitCode !== null, 5000)
    link.close()
    return measured
  }

  // 期望值留了余量：机器忙的时候解码、定时器都会慢一点
  await scenario('300 kbps、单程 50 ms', { kbps: 300, delayMs: 50 }, { p90: 2000, freeze: 4000, fps: 1.5, kbps: 330, slow: true })
  await scenario('1 Mbps、单程 50 ms', { kbps: 1000, delayMs: 50 }, { p90: 1000, freeze: 2000, fps: 5, kbps: 1100 })
  await scenario('8 Mbps、单程 150 ms（带宽够、延迟高）', { kbps: 8000, delayMs: 150 }, { p90: 700, freeze: 1000, fps: 12, slow: false })

  await browser.close()
  cleanup()
  if (COMPARE_ONLY) process.exit(0)
  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch((e) => {
  console.error(e)
  cleanup()
  process.exit(1)
})
