// PWA 离线：应用壳、缩略图缓存、离线发件箱、绕过 SW 的导航、更新提示、退出登录清理
// 用真实 Chromium（headless）验证，内置浏览器面板不能注册 Service Worker，所以离线行为只能这样测。
// 前提：compose 栈已启动（deploy/ 下 docker compose up -d），浏览器已安装（npx playwright install chromium）。
// 用法：cd web && node e2e/<本文件>        可选环境变量：E2E_BASE（默认 http://localhost:8080，必须是安全上下文）
// 会以 admin@example.com / admin123（deploy/.env 的本地测试值）登录；会临时改容器里的 sw.js 模拟发版，结束时还原。
const { chromium } = require('playwright')
const zlib = require('zlib')
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

/** 每次跑生成不同颜色的 PNG，避免命中已有 blob（那样缩略图早就生成了，测不到"先回退原图、后换缩略图"） */
function makePng(w, h) {
  const seed = Math.floor(Math.random() * 255)
  const raw = Buffer.alloc((w * 3 + 1) * h)
  for (let y = 0; y < h; y++) {
    raw[y * (w * 3 + 1)] = 0
    for (let x = 0; x < w; x++) {
      const o = y * (w * 3 + 1) + 1 + x * 3
      raw[o] = (x * 255) / w
      raw[o + 1] = (y * 255) / h
      raw[o + 2] = seed
    }
  }
  const crc = (buf) => {
    let c = ~0
    for (const b of buf) { c ^= b; for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1)) }
    return ~c >>> 0
  }
  const chunk = (t, d) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(d.length)
    const td = Buffer.concat([Buffer.from(t), d])
    const c = Buffer.alloc(4); c.writeUInt32BE(crc(td))
    return Buffer.concat([len, td, c])
  }
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))])
}

async function scrollKick(page) {
  await page.evaluate(() => document.querySelector('[data-testid=message-scroller]')?.dispatchEvent(new Event('scroll')))
}

async function idbCount(page, store) {
  return page.evaluate((s) => new Promise((res) => {
    const o = indexedDB.open('privatechat')
    o.onsuccess = () => {
      const d = o.result
      if (!d.objectStoreNames.contains(s)) { d.close(); res(-1); return }
      const r = d.transaction(s).objectStore(s).count()
      r.onsuccess = () => { d.close(); res(r.result) }
    }
    o.onerror = () => res(-2)
  }), store)
}

;(async () => {
  const browser = await chromium.launch()
  const context = await browser.newContext({ viewport: { width: 1100, height: 800 }, serviceWorkers: 'allow' })
  const page = await context.newPage()
  page.on('pageerror', (e) => console.log('  [pageerror]', String(e).slice(0, 200)))

  // ---------- 1. 登录、SW 接管 ----------
  await page.goto(BASE + '/')
  await page.locator('input[autocomplete=username]').fill('admin@example.com')
  await page.locator('input[type=password]').fill('admin123')
  await page.getByRole('button', { name: '登录' }).click()
  await page.waitForSelector('ul li a', { timeout: 15000 })
  const CID = (await page.getAttribute('ul li a', 'href')).split('/').pop()
  await page.evaluate(() => navigator.serviceWorker.ready)
  await page.reload()
  await page.waitForSelector('ul li a')
  const sw = await page.evaluate(async () => {
    const r = await navigator.serviceWorker.getRegistration()
    return { controlled: !!navigator.serviceWorker.controller, script: r?.active?.scriptURL, waiting: !!r?.waiting }
  })
  check('SW 已激活并接管页面', sw.controlled && sw.script?.endsWith('/sw.js'), sw)
  const manifest = await page.evaluate(async () => (await fetch('/manifest.webmanifest')).json())
  check('manifest 带 PNG 图标与 maskable', manifest.icons?.some((i) => i.purpose === 'maskable') && manifest.icons?.some((i) => i.sizes === '512x512'), manifest.icons?.map((i) => i.src))

  // ---------- 2. 发一张图，等缩略图生成，检查运行时缓存 ----------
  await page.goto(`${BASE}/messages/${CID}`)
  await page.waitForSelector('footer textarea')
  await page.setInputFiles('[data-testid=file-input]', { name: 'e2e.png', mimeType: 'image/png', buffer: makePng(900, 600) })
  const thumbSel = 'img[src*="thumb-320"][src*="r="]'
  let blobId = null
  try {
    await page.waitForFunction((sel) => { document.querySelector('[data-testid=message-scroller]')?.dispatchEvent(new Event('scroll')); return !!document.querySelector(sel) }, thumbSel, { timeout: 30000, polling: 500 })
    const src = await page.getAttribute(thumbSel, 'src')
    blobId = src.match(/\/api\/blobs\/([0-9a-f-]{36})/)[1]
    await page.waitForFunction((sel) => { const i = document.querySelector(sel); return i && i.complete && i.naturalWidth > 0 }, thumbSel, { timeout: 10000 })
  } catch (e) {
    check('图片消息发出且缩略图生成（MessageUpdated 后换成 r=1）', false, String(e).slice(0, 120))
  }
  if (blobId) {
    check('图片消息发出且缩略图生成（MessageUpdated 后换成 r=1）', true, blobId)
    const cached = await page.evaluate(async () => {
      const c = await caches.open('pc-media-v1')
      const keys = await c.keys()
      return Promise.all(keys.map(async (k) => { const r = await c.match(k); return { url: k.url.replace(location.origin, ''), type: r.headers.get('content-type'), cc: r.headers.get('cache-control') } }))
    })
    const mine = cached.filter((k) => k.url.includes(blobId))
    check('缩略图进入运行时缓存，缓存键不含 r，内容是 webp', mine.length >= 1 && mine.every((k) => !k.url.includes('r=') && k.type === 'image/webp'), mine)
    check('回退原图（no-store）没有进缓存', !mine.some((k) => /no-store/.test(k.cc || '')), mine.map((k) => k.cc))
  }

  // ---------- 3. 断网刷新：应用壳、身份、消息、缩略图都来自本地 ----------
  await context.setOffline(true)
  await page.reload()
  await page.waitForSelector('footer textarea', { timeout: 10000 })
  await scrollKick(page)
  await page.waitForTimeout(800)
  const offline = await page.evaluate(() => ({
    onLine: navigator.onLine,
    login: document.body.innerText.includes('本地模式登录'),
    rows: document.querySelectorAll('[data-seq]').length,
    banner: document.querySelector('[data-testid=connection-banner]')?.innerText ?? null,
  }))
  check('断网刷新：页面从 SW 打开、不掉回登录页、消息来自本地', !offline.onLine && !offline.login && offline.rows > 0, offline)
  check('断网横幅提示', (offline.banner || '').includes('网络已断开'), offline.banner)
  if (blobId) {
    const imgOk = await page.waitForFunction((id) => {
      document.querySelector('[data-testid=message-scroller]')?.dispatchEvent(new Event('scroll'))
      const i = document.querySelector(`img[src*="${id}"]`)
      return i && i.complete && i.naturalWidth > 0 ? i.getAttribute('src') : false
    }, blobId, { timeout: 8000, polling: 300 }).then((h) => h.jsonValue()).catch(() => null)
    check('断网时之前看过的缩略图仍然显示（命中缓存）', !!imgOk, imgOk)
  }
  const otherPage = await context.newPage()
  await otherPage.goto(BASE + '/settings').catch(() => undefined)
  // 懒加载的设置页在 load 事件之后才渲染出来：等它出现，而不是在 load 那一刻就判断
  const settingsOffline = await otherPage.waitForFunction(() => document.body.innerText.includes('退出登录'), null, { timeout: 10000 })
    .then(() => true).catch(() => false)
  check('断网时直接打开其它路由（/settings，懒加载分片）也能显示', settingsOffline)
  await otherPage.close()

  // ---------- 4. 断网发消息 → 排队 → 刷新仍在 → 联网自动发出 ----------
  const text = `断网时发的 ${Date.now()}`
  await page.locator('footer textarea').fill(text)
  await page.locator('footer textarea').press('Enter')
  await page.waitForTimeout(500)
  await scrollKick(page)
  const queued = await page.evaluate((t) => [...document.querySelectorAll('[data-seq]')].find((e) => e.innerText.includes(t))?.innerText.replace(/\n/g, ' '), text)
  check('断网发出的消息显示"等待网络"', (queued || '').includes('等待网络'), queued)
  check('消息写入发件箱', (await idbCount(page, 'outbox')) === 1)
  await page.reload()
  await page.waitForSelector('footer textarea')
  await page.waitForTimeout(600)
  await scrollKick(page)
  const afterReload = await page.evaluate((t) => [...document.querySelectorAll('[data-seq]')].some((e) => e.innerText.includes(t)), text)
  check('断网刷新后排队消息还在', afterReload)
  await context.setOffline(false)
  const delivered = await page.waitForFunction((t) => {
    document.querySelector('[data-testid=message-scroller]')?.dispatchEvent(new Event('scroll'))
    const row = [...document.querySelectorAll('[data-seq]')].find((e) => e.innerText.includes(t))
    return row && !row.innerText.includes('等待网络') && !row.innerText.includes('发送中') && Number(row.dataset.seq) < 9e15
  }, text, { timeout: 45000, polling: 500 }).then(() => true).catch(() => false)
  check('恢复联网后自动发出（拿到服务器 seq）', delivered)
  const serverCount = Number(execSync(`${COMPOSE} exec -T postgres psql -U privatechat -d privatechat -At -c "select count(*) from messages where body = '${text}'"`).toString().trim())
  check('服务器上恰好一条，不重复', serverCount === 1, serverCount)
  check('发件箱清空', (await idbCount(page, 'outbox')) === 0)

  // ---------- 5. 必须绕过 SW 的导航 ----------
  const seen = []
  const onResp = (r) => { if (/\/api\/auth\/reauth|\/cdn-cgi\//.test(r.url())) seen.push({ url: r.url().replace(BASE, ''), status: r.status(), fromSW: r.fromServiceWorker() }) }
  page.on('response', onResp)
  await page.goto(BASE + '/api/auth/reauth?returnUrl=%2Fsettings')
  await page.waitForURL('**/settings')
  check('重新验证跳板走网络（不被 SW 拦）并回到原页面', seen.some((s) => s.url.startsWith('/api/auth/reauth') && s.status === 302 && !s.fromSW) && page.url().endsWith('/settings'), seen)
  await page.goto(BASE + '/cdn-cgi/access/logout').catch(() => undefined)
  check('/cdn-cgi/ 导航走网络（Access 退出能到达 Cloudflare）', seen.some((s) => s.url.startsWith('/cdn-cgi/') && !s.fromSW), seen.filter((s) => s.url.startsWith('/cdn-cgi/')))
  page.off('response', onResp)

  // ---------- 6. 新版本：提示条 → 点更新 → 新 SW 接管 ----------
  await page.goto(BASE + '/messages')
  await page.waitForSelector('ul li a')
  bumpServiceWorker(Date.now())
  await page.evaluate(async () => (await navigator.serviceWorker.getRegistration()).update())
  const prompt = await page.waitForSelector('[data-testid=update-prompt]', { timeout: 20000 }).then(() => true).catch(() => false)
  check('部署新版本后出现"有新版本"提示（不自动刷新）', prompt)
  if (prompt) {
    await Promise.all([page.waitForEvent('load', { timeout: 20000 }), page.click('[data-testid=update-prompt] button')])
    await page.waitForSelector('ul li a')
    const afterUpdate = await page.evaluate(async () => {
      const r = await navigator.serviceWorker.getRegistration()
      return { controlled: !!navigator.serviceWorker.controller, waiting: !!r?.waiting, prompt: !!document.querySelector('[data-testid=update-prompt]') }
    })
    check('点"立即更新"后新 SW 接管、提示消失', afterUpdate.controlled && !afterUpdate.waiting && !afterUpdate.prompt, afterUpdate)
  }

  // ---------- 7. 退出登录清空本机数据 ----------
  await page.goto(BASE + '/settings')
  await page.click('[data-testid=logout]')
  await page.waitForSelector('.el-message-box')
  await Promise.all([page.waitForEvent('load', { timeout: 20000 }), page.click('.el-message-box__btns .el-button--primary')])
  await page.waitForSelector('input[type=password]', { timeout: 15000 })
  const wiped = await page.evaluate(async () => ({
    user: localStorage.getItem('pc.session.user'),
    owner: localStorage.getItem('pc.data.owner'),
    mediaCache: await caches.has('pc-media-v1'),
    tus: Object.keys(localStorage).filter((k) => k.startsWith('tus::')).length,
  }))
  const msgs = await idbCount(page, 'messages')
  check('退出登录：身份、缩略图缓存、tus 指纹清空', !wiped.user && !wiped.owner && !wiped.mediaCache && wiped.tus === 0, wiped)
  check('退出登录：IndexedDB 消息清空', msgs <= 0, msgs)

  // ---------- 8. 退出后断网打开：登录页提示离线 ----------
  await context.setOffline(true)
  await page.reload()
  const offlineLogin = await page.waitForSelector('text=当前没有网络', { timeout: 8000 }).then(() => true).catch(() => false)
  check('未登录且离线：登录页提示需要联网', offlineLogin)
  await context.setOffline(false)

  await browser.close()
  restoreServiceWorker()
  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch((e) => {
  restoreServiceWorker()
  console.error(e)
  process.exit(2)
})
