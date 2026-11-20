// 远程协助两条线路的实测（手动跑，不在 npm run e2e 里）：同一台被控端、同一个"忙碌的测试画面"（--source busy），
// 先在标准线路（经我们的中继）上量端到端延迟和帧率，再切到快速线路（经 Cloudflare Realtime）量一遍。
// 快速线路走的是真的 Cloudflare：api 要配着真实凭据，会产生一点点流量，所以要 E2E_REAL_CF=1 才跑。
// 用法：cd web && E2E_REAL_CF=1 node e2e/remote-lines.cjs      （E2E_SECONDS=每条线路量多少秒，默认 30）
//
// 怎么读这些数字：被控端和浏览器都在这台电脑上。标准线路在这里是"本机 → 本机的服务器 → 本机"，
// 比真实部署（出公网到 Cloudflare、经隧道回家里的服务器、再出去）快得多；快速线路则是真的出去到 Cloudflare 再回来。
// 所以这里能看出快速线路实际是什么水平，看不出真实部署上两条线路差多少。
const fs = require('fs')
const os = require('os')
const path = require('path')
const { chromium } = require('playwright')
const { execSync, spawn } = require('child_process')
const { sample, summarize } = require('./timecode.cjs')

const BASE = process.env.E2E_BASE || 'http://localhost:8080'
const ROOT = path.resolve(__dirname, '../..')
const COMPOSE = process.env.E2E_COMPOSE || `docker compose -f "${path.resolve(ROOT, 'deploy/docker-compose.yml')}"`
// E2E_AGENT_EXE=别的 PrivateChat.Agent.exe：拿发布出来的安装包（单文件版）跑，而不是本机的调试版
const AGENT = process.env.E2E_AGENT_EXE || path.resolve(ROOT, 'src/PrivateChat.Agent/bin/Debug/net11.0-windows/PrivateChat.Agent.exe')
const SECONDS = Number(process.env.E2E_SECONDS || 30)
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

let agent = null
let dir = null
function cleanup() {
  try { agent?.kill() } catch { /* 已退出 */ }
  if (dir) try { fs.rmSync(dir, { recursive: true, force: true }) } catch { /* 进程还占着 */ }
}

;(async () => {
  if (process.env.E2E_REAL_CF !== '1') {
    console.log('这个脚本会经真实的 Cloudflare Realtime 传画面（一点点流量）。要跑请加 E2E_REAL_CF=1')
    process.exit(2)
  }
  const realtime = execSync(`${COMPOSE} exec -T api printenv CloudflareRealtime__BaseUrl CloudflareRealtime__AppId`).toString().trim().split(/\r?\n/)
  if (!/rtc\.live\.cloudflare\.com/.test(realtime[0] ?? '') || !realtime[1] || realtime[1].startsWith('e2e-'))
    throw new Error('api 现在没有连着真实的 Cloudflare Realtime（可能还指着测试用的假 SFU，或者没填凭据）')
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK']) process.on(signal, () => { cleanup(); process.exit(1) })

  execSync(`dotnet build "${path.resolve(ROOT, 'src/PrivateChat.Agent')}" -nologo -v q`, { stdio: 'inherit' })
  const name = '线路对比测试'
  dir = path.join(os.tmpdir(), `pc-agent-lines-${Date.now()}`)
  fs.mkdirSync(dir, { recursive: true })
  let out = ''
  agent = spawn(AGENT, ['--headless', '--server', BASE, '--data-dir', dir, '--name', name, '--consent', 'accept', '--source', 'busy', '--dry-input', path.join(dir, 'input.jsonl')],
    { env: { ...process.env, PRIVATECHAT_AGENT_TEST: '1' } })
  agent.stdout.on('data', (d) => { out += d.toString() })
  agent.stderr.on('data', (d) => { out += d.toString() })
  const code = await until(() => out.match(/DEVICE-CODE: (\d{8})/)?.[1], 30000)
  if (!code) throw new Error(`被控端没有申请到设备码\n${out.slice(-500)}`)

  const browser = await chromium.launch({ channel: 'msedge' })
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, locale: 'zh-CN' })
  // 先在标准线路上量，之后再点过去
  await context.addInitScript(() => {
    try { if (!localStorage.getItem('remote.transport')) localStorage.setItem('remote.transport', 'relay') } catch { /* about:blank */ }
  })
  const page = await context.newPage()
  page.on('pageerror', (e) => console.log('  [pageerror]', String(e).slice(0, 200)))
  await page.goto(BASE + '/')
  await page.locator('input[autocomplete=username]').fill('admin@example.com')
  await page.locator('input[type=password]').fill('admin123')
  await page.getByRole('button', { name: '登录' }).click()
  await page.locator('input[type=password]').waitFor({ state: 'detached', timeout: 15000 })

  await page.goto(BASE + '/devices')
  await page.locator('[data-testid=add-device]').click()
  await page.locator('[data-testid=device-code]').fill(code)
  await page.locator('[data-testid=device-found]').waitFor({ timeout: 15000 })
  await page.locator('[data-testid=confirm-device]').click()
  const card = page.locator('[data-testid=device]', { hasText: name })
  await card.locator('[data-testid=device-online]', { hasText: '在线' }).waitFor({ timeout: 30000 })
  await card.locator('[data-testid=device-control]').click()
  await page.waitForURL(/\/remote\//, { timeout: 15000 })
  if (!await until(async () => (await sample(page, 0.2)).frames.length > 0, 60000, 100)) throw new Error(`一分钟都没看到画面\n${out.slice(-800)}`)

  const statsLine = page.locator('[data-testid=remote-stats]')
  async function measure(label) {
    await sleep(8000) // 让码率估计稳定下来
    const measured = summarize(await sample(page, SECONDS), SECONDS)
    const toolbar = await statsLine.textContent().catch(() => '')
    const size = await page.evaluate(() => { const c = document.querySelector('[data-testid=remote-canvas]'); return `${c.width}x${c.height}` })
    console.log(`\n== ${label} ==  工具栏：${toolbar}；画面 ${size}`)
    console.log(`   看到的帧率 ${measured.fps} 帧/秒；端到端延迟 中位 ${measured.median} ms / 九成以内 ${measured.p90} ms / 最大 ${measured.max} ms；最长一次画面不动 ${measured.longestFreeze} ms`)
    return measured
  }

  const relay = await measure('标准线路（经本机的中继）')
  await page.locator('[data-testid=remote-line-fast]').click()
  const connected = await until(async () => (await statsLine.getAttribute('data-line')) === 'realtime', 30000)
  if (!connected) {
    console.log('\n快速线路没接上：')
    for (const line of out.split(/\r?\n/).filter((l) => l.includes('快速线路')).slice(-6)) console.log('   ' + line)
  }
  const fast = connected ? await measure('快速线路（经真实的 Cloudflare Realtime）') : null
  // 被控端日志里和线路、码率有关的几行（E2E_VERBOSE=1 全打；再加 PRIVATECHAT_AGENT_REALTIME=verbose 还有控制端每秒的汇报）
  const lines = out.split(/\r?\n/).filter((l) => /网络：|快速线路/.test(l))
  for (const line of process.env.E2E_VERBOSE ? lines : lines.slice(-8)) console.log('   ' + line.replace(/^\S+ \[\w+\] /, ''))

  await page.locator('[data-testid=remote-end]').click()
  await page.locator('[data-testid=remote-ended]').waitFor({ timeout: 15000 })
  await page.goto(BASE + '/devices')
  await card.locator('[data-testid=device-more]').click()
  await page.locator('[data-testid=device-revoke]:visible').click()
  await page.locator('.el-message-box__btns .el-button--primary').click()
  await card.waitFor({ state: 'detached', timeout: 15000 })
  await browser.close()
  cleanup()
  console.log('\nRESULT ' + JSON.stringify({ relay, fast }))
  process.exit(fast ? 0 : 1)
})().catch((e) => {
  console.error(e)
  cleanup()
  process.exit(1)
})
