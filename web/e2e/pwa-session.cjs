// PWA 会话与多标签页：更新提示真的刷新、启动不闪横幅、离线拒绝退出、多标签页同步退出、换账号清数据、推送 endpoint 归属
// 用真实 Chromium（headless）验证，内置浏览器面板不能注册 Service Worker，所以离线行为只能这样测。
// 前提：compose 栈已启动（deploy/ 下 docker compose up -d），浏览器已安装（npx playwright install chromium）。
// 用法：cd web && node e2e/<本文件>        可选环境变量：E2E_BASE（默认 http://localhost:8080，必须是安全上下文）
// 会以 admin@example.com / admin123（deploy/.env 的本地测试值）登录；会临时改容器里的 sw.js 模拟发版，结束时还原。
const { chromium } = require('playwright')
const { execSync } = require('child_process')
const path = require('path')
const BASE = process.env.E2E_BASE || 'http://localhost:8080'
const COMPOSE = `docker compose -f "${path.resolve(__dirname, '../../deploy/docker-compose.yml')}"`
/** 模拟发版：给容器里的 sw.js 追加一行注释，浏览器会认为有新版本 */
// 构建出的 sw.js 是一整行、末尾没有换行：追加块注释（仍是合法 JS）制造新版本；先整份备份，结束时原样拷回
function bumpServiceWorker(tag) {
  execSync(`${COMPOSE} exec -T web sh -c "[ -f /tmp/sw.js.orig ] || cp /usr/share/nginx/html/sw.js /tmp/sw.js.orig; printf '%s' '/* e2e-bump ${tag} */' >> /usr/share/nginx/html/sw.js"`)
}
function restoreServiceWorker() {
  try { execSync(`${COMPOSE} exec -T web sh -c "[ -f /tmp/sw.js.orig ] && cp /tmp/sw.js.orig /usr/share/nginx/html/sw.js && rm /tmp/sw.js.orig || true"`) } catch { /* 容器没起来就算了 */ }
}
const results = []
function check(name, ok, detail) {
  results.push({ name, ok: !!ok })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail !== undefined ? '  — ' + JSON.stringify(detail) : ''}`)
}
const sql = (q) => execSync(`${COMPOSE} exec -T postgres psql -U privatechat -d privatechat -At -c "${q}"`).toString().trim()

async function login(page, account) {
  await page.goto(BASE + '/')
  await page.locator('input[autocomplete=username]').fill(account)
  await page.locator('input[type=password]').fill('admin123')
  await page.getByRole('button', { name: '登录' }).click()
}

async function dbNames(page) {
  return page.evaluate(async () => (await indexedDB.databases()).map((d) => d.name))
}

;(async () => {
  sql("insert into users (id, email, username, display_name, password_hash, role, elder_mode, created_at) select '01a1b1b0-0000-7000-8000-0000000000b0', 'bob@example.com', 'bob', '爸爸', password_hash, 'Member', false, now() from users where email='admin@example.com' on conflict (id) do nothing")
  const browser = await chromium.launch()

  // ---------- 1. 首次访问（页面加载时没有 SW 控制）后一直开着，部署新版本 → 点更新必须真的刷新 ----------
  {
    const ctx = await browser.newContext({ viewport: { width: 1100, height: 800 } })
    const page = await ctx.newPage()
    await login(page, 'admin@example.com')
    await page.waitForSelector('ul li a', { timeout: 15000 })
    await page.evaluate(() => navigator.serviceWorker.ready)
    await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 10000 })
    bumpServiceWorker(Date.now())
    await page.evaluate(async () => (await navigator.serviceWorker.getRegistration()).update())
    const prompt = await page.waitForSelector('[data-testid=update-prompt]', { timeout: 20000 }).then(() => true).catch(() => false)
    check('首次访问的页面（加载时无 SW 控制）也会出现更新提示', prompt)
    if (prompt) {
      await page.evaluate(() => { window.__marker = 'old-page' })
      const reloaded = await Promise.all([
        page.waitForEvent('load', { timeout: 15000 }).then(() => true).catch(() => false),
        page.click('[data-testid=update-prompt] button'),
      ]).then(([r]) => r)
      const after = await page.evaluate(async () => ({ marker: window.__marker ?? null, waiting: !!(await navigator.serviceWorker.getRegistration())?.waiting }))
      check('点"立即更新"后页面真的刷新并由新 SW 接管', reloaded && after.marker === null && !after.waiting, { reloaded, ...after })
    }

    // ---------- 2. 正常启动不闪"连不上服务器"横幅 ----------
    await page.goto(BASE + '/messages')
    const seen = await page.evaluate(async () => {
      const texts = new Set()
      const t0 = performance.now()
      while (performance.now() - t0 < 2500) {
        const b = document.querySelector('[data-testid=connection-banner]')
        if (b) texts.add(b.innerText)
        await new Promise((r) => setTimeout(r, 50))
      }
      return [...texts]
    })
    check('正常启动 2.5 秒内没有出现连接横幅', seen.length === 0, seen)
    await ctx.close()
  }

  // ---------- 3. 离线时拒绝退出登录，提示可见（ElMessage 样式已加载） ----------
  const ctx = await browser.newContext({ viewport: { width: 1100, height: 800 } })
  const a = await ctx.newPage()
  await login(a, 'admin@example.com')
  await a.waitForSelector('ul li a', { timeout: 15000 })
  await a.click('ul li a')
  await a.waitForSelector('footer textarea')
  await a.waitForTimeout(800)
  await ctx.setOffline(true)
  await a.goto(BASE + '/settings')
  await a.click('[data-testid=logout]')
  await a.waitForSelector('.el-message-box')
  const boxStyle = await a.evaluate(() => getComputedStyle(document.querySelector('.el-message-box')).backgroundColor)
  check('退出确认框有样式（不是透明裸元素）', boxStyle && boxStyle !== 'rgba(0, 0, 0, 0)', boxStyle)
  await a.click('.el-message-box__btns .el-button--primary')
  const toast = await a.waitForSelector('.el-message', { timeout: 5000 }).then(async (h) => ({
    text: await h.innerText(),
    position: await h.evaluate((e) => getComputedStyle(e).position),
    top: await h.evaluate((e) => Math.round(e.getBoundingClientRect().top)),
  })).catch(() => null)
  check('离线退出被拒绝并给出可见提示', !!toast && toast.text.includes('连不上服务器') && toast.position === 'fixed' && toast.top < 800, toast)
  const still = await a.evaluate(() => !!localStorage.getItem('pc.session.user'))
  check('离线退出没有清掉本机身份（凭据仍有效时不假装退出）', still)
  await ctx.setOffline(false)

  // ---------- 4. 一个标签页退出，另一个标签页随之退出、不把库建回来 ----------
  const b = await ctx.newPage()
  await b.goto(BASE + '/messages')
  await b.waitForSelector('ul li a', { timeout: 15000 })
  await a.goto(BASE + '/settings')
  await a.click('[data-testid=logout]')
  await a.waitForSelector('.el-message-box')
  await Promise.all([a.waitForEvent('load', { timeout: 20000 }), a.click('.el-message-box__btns .el-button--primary')])
  await a.waitForSelector('input[type=password]', { timeout: 15000 })
  const bOut = await b.waitForSelector('input[type=password]', { timeout: 15000 }).then(() => true).catch(() => false)
  check('另一个标签页收到通知后也回到登录页', bOut)
  await b.waitForTimeout(1500)
  const names = await dbNames(b)
  check('另一个标签页没有把已删除的本地库建回来', !names.includes('privatechat'), names)
  await b.close()

  // ---------- 5. 换账号：上一位的本地数据被清掉 ----------
  await login(a, 'admin@example.com')
  await a.waitForSelector('ul li a', { timeout: 15000 })
  // 挑一个爸爸不在里面的会话（全家群只有管理员）：通话测试会建一个两人的单聊，那个爸爸本来就该看到
  const adminOnlyId = sql("select c.id from conversations c where exists (select 1 from conversation_members m join users u on u.id = m.user_id where m.conversation_id = c.id and u.email = 'admin@example.com') and not exists (select 1 from conversation_members m where m.conversation_id = c.id and m.user_id = '01a1b1b0-0000-7000-8000-0000000000b0') limit 1")
  const adminConv = `/messages/${adminOnlyId}`
  await a.goto(BASE + adminConv)
  await a.waitForSelector('footer textarea')
  await a.waitForTimeout(800)
  // 模拟 30 天 Cookie 过期 / 被清，然后由爸爸登录同一个浏览器
  await ctx.clearCookies()
  await a.goto(BASE + '/messages')
  await a.waitForSelector('input[type=password]', { timeout: 15000 })
  const bobLoaded = a.waitForEvent('load', { timeout: 20000 }).then(() => true).catch(() => false)
  await login(a, 'bob@example.com')
  const switched = await bobLoaded
  await a.waitForTimeout(2500)
  const afterSwitch = await a.evaluate(async (href) => {
    const convs = [...document.querySelectorAll('ul li a')].map((x) => x.getAttribute('href'))
    const cached = await new Promise((res) => {
      const o = indexedDB.open('privatechat')
      o.onsuccess = () => {
        const d = o.result
        if (!d.objectStoreNames.contains('conversations')) { d.close(); res([]); return }
        const r = d.transaction('conversations').objectStore('conversations').getAllKeys()
        r.onsuccess = () => { d.close(); res(r.result) }
      }
      o.onerror = () => res(['error'])
    })
    return { user: JSON.parse(localStorage.getItem('pc.session.user') || 'null')?.username, convs, cached, sawAdminConv: convs.includes(href) || cached.includes(href.split('/').pop()) }
  }, adminConv)
  check('换账号：以新账号进入，旧账号的会话不出现在列表和本地库里', switched && afterSwitch.user === 'bob' && !afterSwitch.sawAdminConv, afterSwitch)
  await ctx.close()
  await browser.close()

  // ---------- 6. 服务器：同一推送 endpoint 换人登记后只归新用户 ----------
  const endpoint = `https://push.example.test/e2e/${Date.now()}`
  const keys = { p256dh: 'BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM', auth: 'tBHItJI5svbpez7KI4CCXg' }
  async function subscribeAs(account) {
    const res = await fetch(BASE + '/api/auth/local/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ usernameOrEmail: account, password: 'admin123' }) })
    const cookie = res.headers.get('set-cookie').split(';')[0]
    const r = await fetch(BASE + '/api/push/subscribe', { method: 'POST', headers: { 'Content-Type': 'application/json', cookie }, body: JSON.stringify({ endpoint, keys }) })
    return r.status
  }
  const s1 = await subscribeAs('admin@example.com')
  const s2 = await subscribeAs('bob@example.com')
  const owners = sql(`select u.username from devices d join users u on u.id = d.user_id where d.push_subscription::text like '%${endpoint}%'`)
  check('同一推送 endpoint 被新用户登记后，旧用户的那条被清掉', s1 === 200 && s2 === 200 && owners === 'bob', { s1, s2, owners })

  restoreServiceWorker()
  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch((e) => {
  restoreServiceWorker()
  console.error(e)
  process.exit(2)
})
