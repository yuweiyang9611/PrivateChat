// 远程控制端到端：真实 Edge（WebCodecs 解 H.264）对 compose 栈，加一个本机无界面 Agent。
// Agent 用测试画面（四色象限 + 移动黑块，两块假显示器 1280×720 / 1920×1080），键鼠不注入系统、写到日志文件——
// 不碰这台电脑的真实屏幕和鼠标键盘。E2E_REAL_SCREEN=1 时最后再用真实屏幕跑一遍（只检查尺寸和非黑屏，不截图）。
// 覆盖：下载安装包、设备码添加（管理员替爸爸添加）、主人实时看到在线 / 被远程提醒、远程控制（自动同意）、
// 画面像素、键鼠 / 滚轮 / 文字 / 特殊键 / 剪贴板、画质与切换显示器、结束与记录、只看模式不注入、拒绝、移除电脑。
// 前提：compose 栈已启动，装有 Edge；用法：cd web && node e2e/remote.cjs
const fs = require('fs')
const os = require('os')
const path = require('path')
const { chromium } = require('playwright')
const { execSync, spawn } = require('child_process')

const BASE = process.env.E2E_BASE || 'http://localhost:8080'
const ROOT = path.resolve(__dirname, '../..')
const COMPOSE = `docker compose -f "${path.resolve(ROOT, 'deploy/docker-compose.yml')}"`
const AGENT = path.resolve(ROOT, 'src/PrivateChat.Agent/bin/Debug/net11.0-windows/PrivateChat.Agent.exe')
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

/** 起一个无界面 Agent；返回进程、它的输出（累积）、键鼠日志路径 */
function startAgent(dir, consent, source = 'pattern') {
  fs.mkdirSync(dir, { recursive: true })
  const inputLog = path.join(dir, 'input.jsonl')
  const args = ['--headless', '--server', BASE, '--data-dir', dir, '--name', 'E2E 测试电脑', '--consent', consent]
  if (source === 'pattern') args.push('--source', 'pattern', '--dry-input', inputLog)
  else args.push('--dry-input', inputLog) // 真实屏幕也不注入真实键鼠
  const proc = spawn(AGENT, args, { env: { ...process.env, PRIVATECHAT_AGENT_TEST: '1' } })
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
    // 数据目录里有（已吊销的）设备令牌：测完删掉
    for (const d of [dir, dir + '-real']) try { fs.rmSync(d, { recursive: true, force: true }) } catch { /* 进程还占着 */ }
  }
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK']) process.on(signal, () => { cleanup(); process.exit(1) })

  execSync(`dotnet build "${path.resolve(ROOT, 'src/PrivateChat.Agent')}" -nologo -v q`, { stdio: 'inherit' })
  sql("insert into users (id, email, username, display_name, password_hash, role, elder_mode, created_at) select '01a1b1b0-0000-7000-8000-0000000000b0', 'bob@example.com', 'bob', '爸爸', password_hash, 'Member', false, now() from users where email='admin@example.com' on conflict (id) do nothing")

  // 安装包随 web 镜像发布
  const head = await fetch(BASE + '/downloads/PrivateChat.Agent.exe', { method: 'HEAD' })
  check('web 镜像里带着远程协助程序的安装包', head.ok && Number(head.headers.get('content-length')) > 10e6 && /attachment/.test(head.headers.get('content-disposition') ?? ''),
    { status: head.status, size: head.headers.get('content-length') })

  agent = startAgent(dir, 'accept')
  const code = (await until(() => agent.out.match(/DEVICE-CODE: (\d{8})/)?.[1]))
  check('Agent 第一次运行申请到 8 位设备码', !!code, agent.out.slice(-300))

  const browser = await chromium.launch({ channel: 'msedge' })
  const opts = { viewport: { width: 1280, height: 900 }, locale: 'zh-CN', timezoneId: 'Asia/Shanghai' }
  const adminCtx = await browser.newContext(opts)
  const bobCtx = await browser.newContext(opts)
  const admin = await adminCtx.newPage()
  const bob = await bobCtx.newPage()
  for (const [who, p] of [['admin', admin], ['bob', bob]]) p.on('pageerror', (e) => console.log(`  [${who} pageerror]`, String(e).slice(0, 200)))
  await login(admin, 'admin@example.com')
  await login(bob, 'bob')
  check('Edge 支持 WebCodecs H.264 解码', await admin.evaluate(async () => (await VideoDecoder.isConfigSupported({ codec: 'avc1.4D401F' })).supported))

  // ---- 添加电脑：管理员输入设备码，替爸爸添加 ----
  await bob.goto(BASE + '/devices')
  await admin.goto(BASE + '/devices')
  await admin.locator('[data-testid=add-device]').click()
  const setupCode = await admin.locator('[data-testid=setup-code]').inputValue({ timeout: 10000 }).catch(() => '')
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
  await card.locator('[data-testid=device-control]').click()
  await admin.waitForURL(/\/remote\//, { timeout: 10000 })
  const sessionId = admin.url().split('/remote/')[1]
  const streaming = await until(async () => (await admin.locator('[data-testid=remote-stats]').textContent().catch(() => '')).includes('1280×720'), 20000)
  check('对方同意后画面开始传输（1280×720，流畅档）', !!streaming, await admin.locator('[data-testid=remote-stats]').textContent().catch(() => ''))
  await sleep(800)
  const colors = { tl: await pixel(admin, 0.25, 0.2), tr: await pixel(admin, 0.75, 0.2), bl: await pixel(admin, 0.25, 0.8), br: await pixel(admin, 0.75, 0.8) }
  check('解码出的画面就是测试画面（左上红、右上绿、左下蓝、右下白）',
    near(colors.tl, [255, 0, 0]) && near(colors.tr, [0, 255, 0]) && near(colors.bl, [0, 0, 255]) && near(colors.br, [255, 255, 255]), colors)
  check('爸爸收到"正在被远程"的提醒', await bob.locator('.el-notification', { hasText: '正在控制你的电脑' }).waitFor({ timeout: 10000 }).then(() => true, () => false))
  check('爸爸的设备卡片上显示谁在远程', await bobCard.locator('[data-testid=device-session]', { hasText: '控制' }).waitFor({ timeout: 10000 }).then(() => true, () => false))
  check('Agent 显示红条', agent.out.includes('红条：正在被 admin 远程控制'))
  const stats = await until(async () => {
    const t = await admin.locator('[data-testid=remote-stats]').textContent()
    const fps = Number(t.match(/(\d+) fps/)?.[1] ?? 0)
    return fps >= 5 ? t : null
  }, 10000)
  check('持续出帧（解码帧率 ≥ 5，有往返延迟）', !!stats && / \d+ ms/.test(stats), stats)

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
  await admin.locator('[data-testid=remote-quality-sharp]').click()
  await admin.locator('[data-testid=remote-monitor]').click()
  await admin.locator('.el-select-dropdown__item', { hasText: '显示器 2' }).click()
  const second = await until(async () => (await canvasSize(admin)) === '1920x1080' && near(await pixel(admin, 0.75, 0.8), [255, 255, 0]), 15000)
  check('清晰档 + 切到第二块显示器：1920×1080，右下象限是黄色', !!second, { size: await canvasSize(admin), br: await pixel(admin, 0.75, 0.8) })
  const before2 = inputs(agent).length
  await moveOnCanvas(admin, 0.5, 0.5)
  const move2 = await until(() => inputs(agent).slice(before2).reverse().find((e) => e.t === 'move'), 5000)
  check('第二块显示器上的坐标换算到虚拟桌面（1280 + 960, 540）', !!move2 && Math.abs(move2.x - 2240) <= 4 && Math.abs(move2.y - 540) <= 4, move2)

  // 结束
  await admin.locator('[data-testid=remote-end]').click()
  check('结束后显示"已结束"', await admin.locator('[data-testid=remote-end-reason]', { hasText: '已结束' }).waitFor({ timeout: 10000 }).then(() => true, () => false))
  check('Agent 收起红条', !!(await until(() => agent.out.includes('红条收起'), 5000)))
  const audit = sql(`select end_reason || ',' || bytes_relayed from remote_sessions where id = '${sessionId}'`)
  check('记录里有结束原因和转发流量', audit.startsWith('controller,') && Number(audit.split(',')[1]) > 10_000, audit)

  // ---- 只看模式：点击不会注入 ----
  await admin.goto(BASE + '/devices')
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
  await admin.locator('[data-testid=remote-end]').click()
  await admin.locator('[data-testid=remote-ended]').waitFor({ timeout: 10000 })

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
    check('真实屏幕：GDI 截屏 + 编码 + 解码出非黑画面', !!real, real ?? (await canvasSize(admin)))
    await admin.locator('[data-testid=remote-end]').click()
    await admin.goto(BASE + '/devices')
    await realCard.locator('[data-testid=device-more]').click()
    await admin.locator('[data-testid=device-revoke]:visible').click()
    await admin.locator('.el-message-box__btns .el-button--primary').click()
    agent.proc.kill()
  }

  const errors = execSync(`${COMPOSE} logs api --since ${startedAt}`, { maxBuffer: 256 * 1024 * 1024 }).toString().split('\n').filter((l) => /\bERR\b|Unhandled|Exception/.test(l))
  check('api 日志没有错误', errors.length === 0, errors.slice(0, 3))

  await browser.close()
  cleanup()
  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch((e) => {
  console.error(e)
  try { execSync('taskkill /IM PrivateChat.Agent.exe /F', { stdio: 'ignore' }) } catch { /* 没有残留 */ }
  process.exit(1)
})
