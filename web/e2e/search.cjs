// 搜索端到端：真实 Chromium 对 compose 栈，两个浏览器（admin / bob）。
// 覆盖：会话列表搜索框 → 搜索页；聊天、网盘、时光三类结果和高亮；点消息跳到很早的那条并闪一下（要往前翻好几页）；
// 点图片预览、打开所在文件夹；点评论跳到动态并闪那条评论；私密动态别人搜不到；按类型看；
// 离线时在本机保存过的聊天记录里搜；手机宽度不横向溢出；api 无错误日志。
// 前提：compose 栈已启动，浏览器已安装（npx playwright install chromium）。用法：cd web && node e2e/search.cjs
const path = require('path')
const { chromium } = require('playwright')
const { execSync } = require('child_process')

const BASE = process.env.E2E_BASE || 'http://localhost:8080'
const COMPOSE = `docker compose -f "${path.resolve(__dirname, '../../deploy/docker-compose.yml')}"`
const BOB_ID = '01a1b1b0-0000-7000-8000-0000000000b0'
const results = []
function check(name, ok, detail) {
  results.push({ name, ok: !!ok })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail !== undefined ? '  — ' + JSON.stringify(detail) : ''}`)
}
const sql = (q) => execSync(`${COMPOSE} exec -T postgres psql -U privatechat -d privatechat -At -c "${q}"`).toString().trim()
const ok = (p) => p.then(() => true).catch(() => false)

async function login(page, who) {
  await page.goto(BASE + '/')
  await page.locator('input[autocomplete=username]').fill(who)
  await page.locator('input[type=password]').fill('admin123')
  await page.getByRole('button', { name: '登录' }).click()
  await page.locator('input[type=password]').waitFor({ state: 'detached', timeout: 15000 })
}

/** 在页面里带着登录 Cookie 调接口 */
const call = (page, method, url, body) => page.evaluate(async ([method, url, body]) => {
  const r = await fetch(url, { method, headers: body ? { 'Content-Type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined })
  return { status: r.status, data: r.headers.get('content-type')?.includes('json') ? await r.json() : null }
}, [method, url, body])

;(async () => {
  const startedAt = new Date().toISOString()
  const stamp = Date.now().toString(36)
  const TOKEN = `年夜饭${stamp}`

  sql(`insert into users (id, email, username, display_name, password_hash, role, elder_mode, created_at) select '${BOB_ID}', 'bob@example.com', 'bob', '爸爸', password_hash, 'Member', false, now() from users where email='admin@example.com' on conflict (id) do nothing`)

  const browser = await chromium.launch()
  const opts = { viewport: { width: 1280, height: 860 }, timezoneId: 'Asia/Shanghai', locale: 'zh-CN' }
  const adminCtx = await browser.newContext(opts)
  const bobCtx = await browser.newContext(opts)
  const admin = await adminCtx.newPage()
  const bob = await bobCtx.newPage()
  for (const [who, p] of [['admin', admin], ['bob', bob]]) p.on('pageerror', (e) => console.log(`  [${who} pageerror]`, String(e).slice(0, 200)))
  await login(admin, 'admin@example.com')

  // ---- 准备数据：很早的一条（后面再发 1000 条：登录时同步下来的最近 500 条之外还远，跳过去要直接取它附近一段）、网盘里一张图、一条动态和评论、一条私密动态 ----
  // 每次新建一个群：seq 从 1 开始，和以前跑过的数据无关
  const conv = (await call(admin, 'POST', '/api/conversations', { type: 'Group', title: `搜索测试 ${stamp}`, memberIds: [BOB_ID] })).data.id
  const say = (page, body) => call(page, 'POST', `/api/conversations/${conv}/messages`, { conversationId: conv, clientMessageId: crypto.randomUUID(), type: 'Text', body })
  const early = (await say(admin, `今年的${TOKEN}去奶奶家吃，记得带酒`)).data
  for (let i = 0; i < 1000; i += 25) await Promise.all(Array.from({ length: 25 }, (_, j) => say(admin, `闲聊第 ${i + j} 条`)))
  await login(bob, 'bob')
  await say(bob, `${TOKEN} 我来准备饺子`)

  await admin.goto(BASE + '/drive/family')
  // 上传按钮出现 = 文件夹列表已经加载好（之前选的文件会被忽略，用户也点不到按钮）
  await admin.locator('[data-testid=drive-upload]').waitFor({ timeout: 60000 })
  const jpeg = Buffer.from(await admin.evaluate(() => {
    const c = document.createElement('canvas'); c.width = 800; c.height = 600
    const g = c.getContext('2d'); g.fillStyle = '#e76f51'; g.fillRect(0, 0, 800, 600)
    return c.toDataURL('image/jpeg', 0.9).split(',')[1]
  }), 'base64')
  await admin.setInputFiles('[data-testid=drive-file-input]', [{ name: `${TOKEN}合影.jpg`, mimeType: 'image/jpeg', buffer: jpeg }])
  const uploaded = await ok(admin.waitForFunction((name) => [...document.querySelectorAll('[data-upload-status]')].some((e) => e.getAttribute('data-upload-status') === 'done' && e.textContent.includes(name)), `${TOKEN}合影.jpg`, { timeout: 90000 }))
  check('图片上传到家庭网盘', uploaded, uploaded ? undefined : await admin.evaluate(() => [...document.querySelectorAll('[data-upload-status]')].map((e) => e.getAttribute('data-upload-status') + ' ' + e.textContent.slice(0, 120))))

  const post = (await call(bob, 'POST', '/api/posts', { text: `今天的${TOKEN}太丰盛了`, visibility: 'Family', blobIds: [] })).data
  const withComment = (await call(admin, 'POST', `/api/posts/${post.id}/comments`, { text: `${TOKEN}的鱼最好吃` })).data
  const commentId = withComment.comments.at(-1).id
  await call(admin, 'POST', '/api/posts', { text: `${TOKEN}私密日记`, visibility: 'Private', blobIds: [] })

  // ---- 从会话列表的搜索框进去 ----
  await admin.goto(BASE + '/messages')
  await admin.locator('[data-testid=conversation-search]').fill(TOKEN)
  await admin.locator('[data-testid=search-everything]').click()
  await admin.waitForURL(/\/search\?q=/)
  await admin.locator('[data-testid=search-message]').first().waitFor({ timeout: 15000 })
  const counts = {
    messages: await admin.locator('[data-testid=search-message]').count(),
    files: await admin.locator('[data-testid=search-file]').count(),
    moments: await admin.locator('[data-testid=search-moment]').count(),
  }
  check('搜索页三类都有结果：聊天 2、文件 1、时光 3（含自己的私密动态）', counts.messages === 2 && counts.files === 1 && counts.moments === 3, counts)
  check('命中的词高亮', (await admin.locator('[data-testid=search-message] mark').first().textContent()) === TOKEN)

  // 点很早的那条：用一个新打开的浏览器（本机没缓存这么早的消息）跳过去——直接取它附近一段，滚过去闪一下，地址里的 seq 去掉；
  // 它和最新的消息之间有空档，点"加载中间的消息"补上
  const jumperCtx = await browser.newContext(opts)
  const jumper = await jumperCtx.newPage()
  await login(jumper, 'admin@example.com')
  await jumper.goto(`${BASE}/search?q=${encodeURIComponent(TOKEN)}`)
  await jumper.locator(`[data-testid=search-message][data-message="${early.id}"]`).click()
  await jumper.waitForURL(new RegExp(`/messages/${conv}`))
  const row = jumper.locator(`[data-seq="${early.seq}"][data-highlighted]`)
  check('跳到 1000 条之前的那条消息并高亮', await ok(row.waitFor({ timeout: 20000 })))
  check('那条消息在可视区域里', await row.evaluate((el) => {
    const r = el.getBoundingClientRect()
    const s = document.querySelector('[data-testid=message-scroller]').getBoundingClientRect()
    return r.bottom > s.top && r.top < s.bottom
  }).catch(() => false))
  await jumper.waitForFunction(() => !location.search.includes('seq='), null, { timeout: 5000 }).catch(() => undefined)
  check('跳完把 seq 参数去掉', !jumper.url().includes('seq='))
  const gap = jumper.locator('[data-testid=message-gap]').first()
  // 空档在目标下面二十几条处（列表是虚拟滚动，只渲染视口附近）：往下滚到它出现
  for (let i = 0; i < 40 && !(await gap.count()); i++) {
    await jumper.locator('[data-testid=message-scroller]').evaluate((el) => el.scrollBy(0, 300))
    await jumper.waitForTimeout(100)
  }
  const gapShown = await ok(gap.waitFor({ timeout: 5000 }))
  const newer = Number(await gap.getAttribute('data-newer').catch(() => 0))
  const older = Number(await gap.getAttribute('data-older').catch(() => 0))
  check('那一段和最新消息之间显示"加载中间的消息"', gapShown && newer > early.seq + 25, { newer, early: early.seq })
  await gap.click()
  check('点了之后从那一段往后接着补（往下读不会断）', await ok(jumper.locator(`[data-seq="${older + 1}"]`).waitFor({ state: 'attached', timeout: 10000 })))
  await jumperCtx.close()

  // 图片：点开预览；"所在文件夹"打开家庭网盘
  await admin.locator('[data-testid=search-file]').first().waitFor({ timeout: 10000 })
  await admin.locator('[data-testid=search-file] button').first().click()
  check('点图片打开预览', await ok(admin.locator('.el-image-viewer__wrapper').waitFor({ timeout: 10000 })))
  await admin.keyboard.press('Escape')
  await admin.locator('[data-testid=search-file-folder]').first().click()
  await admin.waitForURL(/\/drive\/family/)
  check('"所在文件夹"打开家庭网盘', await ok(admin.locator('[data-testid=drive-file-input]').waitFor({ state: 'attached', timeout: 10000 })))

  // 评论：跳到那条动态并闪一下那条评论
  await admin.goBack()
  await admin.locator(`[data-testid=search-moment][data-kind=comment][data-id="${commentId}"]`).click()
  await admin.waitForURL(new RegExp(`/moments/posts/${post.id}`))
  check('点评论跳到动态并高亮那条评论', await ok(admin.locator(`[data-comment="${commentId}"].pc-flash`).waitFor({ timeout: 10000 })))

  // 按类型看
  await admin.goto(`${BASE}/search?q=${encodeURIComponent(TOKEN)}`)
  await admin.locator('[data-testid=search-tab-Moments]').click()
  await admin.waitForURL(/type=Moments/)
  await admin.waitForFunction(() => document.querySelectorAll('[data-testid=search-moment]').length === 3 && !document.querySelector('[data-testid=search-message]'), null, { timeout: 10000 }).catch(() => undefined)
  check('"时光"页签只显示时光的结果', (await admin.locator('[data-testid=search-moment]').count()) === 3 && (await admin.locator('[data-testid=search-file]').count()) === 0)

  // ---- 爸爸：看不到别人的私密动态；离线时搜本机保存过的聊天 ----
  await bob.goto(`${BASE}/search?q=${encodeURIComponent(TOKEN)}`)
  await bob.locator('[data-testid=search-moment]').first().waitFor({ timeout: 15000 })
  const bobMoments = await bob.locator('[data-testid=search-moment]').allTextContents()
  check('别人的私密动态搜不到', bobMoments.length === 2 && !bobMoments.some((t) => t.includes('私密日记')), bobMoments)

  await bob.goto(`${BASE}/messages/${conv}`) // 打开会话：最近 50 条存进本机
  await bob.locator('[data-testid=message-scroller]').waitFor()
  await bob.waitForFunction((t) => document.body.innerText.includes(t), `${TOKEN} 我来准备饺子`, { timeout: 15000 })
  await bob.locator('[data-testid=conversation-search]').fill(TOKEN)
  await bob.locator('[data-testid=search-everything]').click()
  await bob.waitForURL(/\/search\?q=/)
  await bob.locator('[data-testid=search-message]').first().waitFor({ timeout: 15000 })
  await bobCtx.setOffline(true)
  const offline = await ok(bob.locator('[data-testid=search-offline]').waitFor({ timeout: 10000 }))
  const offlineHits = await bob.locator('[data-testid=search-message]').allTextContents()
  const serverOnly = (await bob.locator('[data-testid=search-file]').count()) + (await bob.locator('[data-testid=search-moment]').count())
  check('离线：提示只搜本机保存的聊天记录（登录时同步下来的也算），文件和时光不显示', offline && offlineHits.some((t) => t.includes('我来准备饺子')) && serverOnly === 0, { offlineHits, serverOnly })
  await bobCtx.setOffline(false)

  // ---- 手机宽度 ----
  await admin.setViewportSize({ width: 375, height: 760 })
  await admin.goto(`${BASE}/search?q=${encodeURIComponent(TOKEN)}`)
  await admin.locator('[data-testid=search-message]').first().waitFor({ timeout: 15000 })
  check('手机宽度不横向溢出', await admin.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1))

  const errors = execSync(`${COMPOSE} logs api --since ${startedAt}`, { maxBuffer: 256 * 1024 * 1024 }).toString().split('\n').filter((l) => / (ERR|FTL)\]|fail:/.test(l))
  check('api 日志没有错误', errors.length === 0, errors.slice(0, 3))

  await browser.close()
  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch((e) => {
  console.error(e)
  process.exit(2)
})
