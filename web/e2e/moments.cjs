// 时光端到端：真实 Chromium（headless）对 compose 栈，两个浏览器（admin / bob）。
// 覆盖：发带照片的动态、家人实时看到、点赞 / 评论 / 回复实时同步、私密动态、那年今天、
// 相册（新建、从时间线添加、家人可见、封面）、按月相册、删除动态实时消失、手机宽度布局、api 无错误日志。
// 前提：compose 栈已启动，浏览器已安装（npx playwright install chromium）。用法：cd web && node e2e/moments.cjs
const path = require('path')
const { chromium } = require('playwright')
const { execSync } = require('child_process')

const BASE = process.env.E2E_BASE || 'http://localhost:8080'
const COMPOSE = `docker compose -f "${path.resolve(__dirname, '../../deploy/docker-compose.yml')}"`
const results = []
function check(name, ok, detail) {
  results.push({ name, ok: !!ok })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail !== undefined ? '  — ' + JSON.stringify(detail) : ''}`)
}
const sql = (q) => execSync(`${COMPOSE} exec -T postgres psql -U privatechat -d privatechat -At -c "${q}"`).toString().trim()

/** 在 canvas JPEG 的 SOI 之后插入只含 DateTimeOriginal 的 EXIF APP1 段（与 drive.cjs 相同） */
function withExifDate(jpeg, date) {
  const tiff = Buffer.alloc(64)
  tiff.write('II', 0, 'ascii'); tiff.writeUInt16LE(42, 2); tiff.writeUInt32LE(8, 4)
  tiff.writeUInt16LE(1, 8)
  tiff.writeUInt16LE(0x8769, 10); tiff.writeUInt16LE(4, 12); tiff.writeUInt32LE(1, 14); tiff.writeUInt32LE(26, 18)
  tiff.writeUInt32LE(0, 22)
  tiff.writeUInt16LE(1, 26)
  tiff.writeUInt16LE(0x9003, 28); tiff.writeUInt16LE(2, 30); tiff.writeUInt32LE(20, 32); tiff.writeUInt32LE(44, 36)
  tiff.writeUInt32LE(0, 40)
  tiff.write(date + '\0', 44, 'ascii')
  const head = Buffer.from('Exif\0\0', 'binary')
  const len = Buffer.alloc(2); len.writeUInt16BE(2 + head.length + tiff.length)
  const app1 = Buffer.concat([Buffer.from([0xff, 0xe1]), len, head, tiff])
  return Buffer.concat([jpeg.subarray(0, 2), app1, jpeg.subarray(2)])
}

async function login(page, who) {
  await page.goto(BASE + '/')
  await page.locator('input[autocomplete=username]').fill(who)
  await page.locator('input[type=password]').fill('admin123')
  await page.getByRole('button', { name: '登录' }).click()
  await page.locator('input[type=password]').waitFor({ state: 'detached', timeout: 15000 })
}

async function makeJpeg(page, label, exifDate) {
  const b64 = await page.evaluate((text) => {
    const c = document.createElement('canvas'); c.width = 1200; c.height = 900
    const g = c.getContext('2d'); const grd = g.createLinearGradient(0, 0, 1200, 900)
    grd.addColorStop(0, '#e76f51'); grd.addColorStop(1, '#264653'); g.fillStyle = grd; g.fillRect(0, 0, 1200, 900)
    g.fillStyle = '#fff'; g.font = '90px sans-serif'; g.fillText(text, 80, 480)
    return c.toDataURL('image/jpeg', 0.9).split(',')[1]
  }, label + ' ' + Math.random().toString(36).slice(2, 6))
  return withExifDate(Buffer.from(b64, 'base64'), exifDate)
}

const ok = (p) => p.then(() => true).catch(() => false)
const postCard = (page, text) => page.locator('[data-testid=post-card]', { hasText: text }).first()

;(async () => {
  const startedAt = new Date().toISOString()
  const stamp = Date.now()
  // 家庭时区的今天：照片的拍摄时间设成 5 年前的今天，用来验证"那年今天"
  const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Shanghai' }).format(new Date())
  const [ty, tm, td] = today.split('-')
  const oldYear = Number(ty) - 5

  sql("insert into users (id, email, username, display_name, password_hash, role, elder_mode, created_at) select '01a1b1b0-0000-7000-8000-0000000000b0', 'bob@example.com', 'bob', '爸爸', password_hash, 'Member', false, now() from users where email='admin@example.com' on conflict (id) do nothing")

  const browser = await chromium.launch()
  const opts = { viewport: { width: 1200, height: 860 }, timezoneId: 'Asia/Shanghai', locale: 'zh-CN' }
  const adminCtx = await browser.newContext(opts)
  const bobCtx = await browser.newContext(opts)
  const admin = await adminCtx.newPage()
  const bob = await bobCtx.newPage()
  for (const [who, p] of [['admin', admin], ['bob', bob]]) p.on('pageerror', (e) => console.log(`  [${who} pageerror]`, String(e).slice(0, 200)))
  await login(admin, 'admin@example.com')
  await login(bob, 'bob')

  // Bob 先打开时光页，等着看实时到达的新动态
  await bob.goto(BASE + '/moments')
  await bob.locator('[data-testid=moments-feed]').waitFor()

  // ---------- 发一条带照片的动态 ----------
  const text = `周末包饺子 ${stamp}`
  await admin.goto(BASE + '/moments')
  await admin.locator('[data-testid=moments-feed]').waitFor()
  await admin.click('[data-testid=new-post]')
  await admin.locator('[data-testid=composer-text]').fill(text)
  const jpeg = await makeJpeg(admin, `${oldYear} 饺子`, `${oldYear}:${tm}:${td} 10:00:00`)
  await admin.setInputFiles('[data-testid=composer-file]', [{ name: '饺子.jpg', mimeType: 'image/jpeg', buffer: jpeg }])
  const publishReady = await ok(admin.waitForFunction(() => {
    const b = document.querySelector('[data-testid=composer-publish]')
    return b && !b.disabled && !b.classList.contains('is-disabled') && b.textContent.includes('发布')
  }, null, { timeout: 30000 }))
  check('选的照片上传完成后才能发布', publishReady)
  await admin.click('[data-testid=composer-publish]')
  await postCard(admin, text).waitFor({ timeout: 10000 })
  const first = await admin.locator('[data-testid=post-card]').first().innerText()
  check('发布后出现在自己时间线的最前面', first.includes(text))
  const postId = await postCard(admin, text).getAttribute('data-post')
  const blobId = sql(`select blob_id from post_media where post_id = '${postId}'`)
  check('动态带着一张照片', /^[0-9a-f-]{36}$/.test(blobId), blobId)

  const bobSees = await ok(postCard(bob, text).waitFor({ timeout: 10000 }))
  check('家人不用刷新就看到新动态（实时）', bobSees)
  const thumb = await ok(bob.waitForFunction((id) => {
    const i = document.querySelector(`[data-post="${id}"] img`)
    return i && i.complete && i.naturalWidth > 0
  }, postId, { timeout: 60000, polling: 1000 }))
  check('家人那边照片缩略图能显示', thumb)

  // ---------- 点赞、评论、回复，双方实时同步 ----------
  await postCard(bob, text).locator('[data-testid=post-like]').click()
  const likeSeen = await ok(admin.waitForFunction((id) => document.querySelector(`[data-post="${id}"] [data-testid=post-reactions]`)?.textContent?.includes('爸爸'), postId, { timeout: 10000 }))
  check('家人点赞后作者实时看到"❤️ 爸爸"', likeSeen)

  await postCard(bob, text).locator('[data-testid=post-comment]').click()
  await postCard(bob, text).locator('[data-testid=comment-input]').fill('看着就香')
  await postCard(bob, text).locator('[data-testid=comment-send]').click()
  const commentSeen = await ok(admin.waitForFunction((id) => document.querySelector(`[data-post="${id}"] [data-testid=comment]`)?.textContent?.includes('看着就香'), postId, { timeout: 10000 }))
  check('家人的评论实时出现在作者那边', commentSeen)

  // 作者点评论回复
  await postCard(admin, text).locator('[data-testid=comment]', { hasText: '看着就香' }).locator('button').first().click()
  const placeholder = await postCard(admin, text).locator('[data-testid=comment-input]').getAttribute('placeholder')
  check('点评论就是回复这个人', placeholder === '回复 爸爸', placeholder)
  await postCard(admin, text).locator('[data-testid=comment-input]').fill('晚上来吃')
  await postCard(admin, text).locator('[data-testid=comment-send]').click()
  const replySeen = await ok(bob.waitForFunction((id) => [...document.querySelectorAll(`[data-post="${id}"] [data-testid=comment]`)]
    .some((c) => /回复s*我：晚上来吃/.test(c.textContent)), postId, { timeout: 10000 }))
  // 被回复的人自己看到的是"X 回复 我：…"
  check('回复实时到达，被回复的人看到"回复 我"', replySeen)

  // 评论的人能删自己的评论
  await postCard(bob, text).locator('[data-testid=comment]', { hasText: '看着就香' }).locator('[data-testid=comment-delete]').click()
  await bob.locator('.el-message-box__btns .el-button--primary').click()
  const commentGone = await ok(admin.waitForFunction((id) => ![...document.querySelectorAll(`[data-post="${id}"] [data-testid=comment]`)].some((c) => c.textContent.includes('看着就香')), postId, { timeout: 10000 }))
  check('删掉的评论双方都消失', commentGone)

  // ---------- 私密动态 ----------
  const secret = `只给自己看 ${stamp}`
  await admin.click('[data-testid=new-post]')
  await admin.locator('[data-testid=composer-text]').fill(secret)
  await admin.locator('[data-testid=composer-visibility] label', { hasText: '仅自己' }).click()
  await admin.click('[data-testid=composer-publish]')
  await postCard(admin, secret).waitFor({ timeout: 10000 })
  check('私密动态带"仅自己可见"标记', (await postCard(admin, secret).locator('[data-testid=post-private]').count()) === 1)
  await bob.waitForTimeout(1500)
  await bob.reload()
  await postCard(bob, text).waitFor({ timeout: 10000 })
  check('家人看不到私密动态', (await postCard(bob, secret).count()) === 0)

  // ---------- 那年今天 ----------
  let onThisDay = false
  for (let i = 0; i < 20 && !onThisDay; i++) {
    await admin.goto(BASE + '/moments')
    onThisDay = await ok(admin.locator(`[data-testid=on-this-day-year][data-year="${oldYear}"] [data-blob="${blobId}"]`).waitFor({ timeout: 2000 }))
  }
  const otdText = onThisDay ? await admin.locator(`[data-testid=on-this-day-year][data-year="${oldYear}"]`).innerText() : ''
  check('5 年前今天拍的照片出现在"那年今天"', onThisDay && otdText.includes('5 年前'), otdText.split('\n')[0])

  // ---------- 相册 ----------
  const albumTitle = `饺子宴-${stamp}`
  await admin.click('[data-testid=moments-tab-albums]')
  await admin.locator('[data-testid=albums]').waitFor()
  await admin.click('[data-testid=album-new]')
  await admin.locator('.el-message-box__input input').fill(albumTitle)
  await admin.locator('.el-message-box__btns .el-button--primary').click()
  await admin.waitForURL(/\/moments\/albums\/[0-9a-f-]{36}$/)
  const albumId = admin.url().split('/').pop()
  await admin.locator('[data-testid=album-heading]', { hasText: albumTitle }).waitFor()
  check('新建相册后进入相册页', true)

  await admin.click('[data-testid=album-add]')
  const picker = admin.locator('[data-testid=media-picker]')
  await picker.locator('[data-testid=picker-item]').first().waitFor({ timeout: 10000 })
  // 时间线里可能已有很多照片：一页页往下找刚才那张
  for (let i = 0; i < 20 && !(await picker.locator(`[data-blob="${blobId}"]`).count()); i++) {
    const more = picker.getByRole('button', { name: '加载更多' })
    if (!(await more.count())) break
    await more.click()
    await admin.waitForTimeout(500)
  }
  await picker.locator(`[data-blob="${blobId}"] button`).first().click()
  await picker.locator('[data-testid=picker-confirm]').click()
  await admin.locator(`[data-testid=album-item] [data-blob="${blobId}"]`).waitFor({ timeout: 10000 })
  check('从时间线把照片加进相册', true)
  check('只有一张时它就是封面', (await admin.locator('[data-testid=album-item]', { hasText: '封面' }).count()) === 1)

  await bob.goto(BASE + '/moments/albums')
  const bobAlbum = await ok(bob.locator(`[data-testid=album-card][data-album="${albumId}"]`, { hasText: albumTitle }).waitFor({ timeout: 10000 }))
  check('家人在相册列表里看到这个相册', bobAlbum)
  await bob.locator(`[data-testid=album-card][data-album="${albumId}"] a`).click()
  const bobItem = await ok(bob.locator(`[data-testid=album-item] [data-blob="${blobId}"]`).waitFor({ timeout: 10000 }))
  check('家人能打开相册看到照片', bobItem)
  check('家人不能改名或删除别人建的相册', (await bob.locator('[data-testid=album-rename], [data-testid=album-delete]').count()) === 0)

  // ---------- 按月相册 ----------
  const month = `${oldYear}-${tm}`
  await bob.goto(BASE + '/moments/albums')
  const monthCard = await ok(bob.locator(`[data-testid=month-card][data-month="${month}"]`).waitFor({ timeout: 10000 }))
  check('按月相册里有这张照片所在的月份', monthCard, month)
  await bob.locator(`[data-testid=month-card][data-month="${month}"] a`).click()
  const inMonth = await ok(bob.locator(`[data-testid=month-item] [data-blob="${blobId}"]`).waitFor({ timeout: 10000 }))
  check('打开按月相册能看到它', inMonth)

  // ---------- 删除动态：家人那边实时消失；推送里的链接显示已删除 ----------
  await bob.goto(BASE + '/moments')
  await postCard(bob, text).waitFor({ timeout: 10000 })
  await admin.goto(BASE + '/moments')
  await postCard(admin, text).locator('[data-testid=post-menu]').click()
  await admin.locator('[data-testid=post-delete]:visible').click()
  await admin.locator('.el-message-box__btns .el-button--primary').click()
  const goneForBob = await ok(bob.waitForFunction((id) => !document.querySelector(`[data-post="${id}"]`), postId, { timeout: 10000 }))
  check('作者删除后家人那边实时消失', goneForBob)
  await bob.goto(`${BASE}/moments/posts/${postId}`)
  check('打开已删除动态的链接显示"不存在或已删除"', await ok(bob.locator('[data-testid=post-gone]').waitFor({ timeout: 10000 })))
  // 照片还在相册里，所以家人仍然能看
  const stillInAlbum = await bob.evaluate(async (id) => (await fetch(`/api/blobs/${id}?v=thumb-320`)).status, blobId)
  check('动态删了，照片仍因为在相册里而可见', stillInAlbum === 200, stillInAlbum)

  // ---------- 手机宽度 ----------
  await bob.setViewportSize({ width: 375, height: 760 })
  await bob.goto(BASE + '/moments')
  await bob.locator('[data-testid=moments-feed]').waitFor()
  await bob.waitForTimeout(500)
  const overflow = await bob.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1)
  const tabs = await bob.locator('[data-testid^=moments-tab-]:visible').count()
  check('手机宽度：分区和"发动态"可用、无横向溢出', !overflow && tabs === 2 && (await bob.locator('[data-testid=new-post]:visible').count()) === 1, { overflow, tabs })
  await bob.screenshot({ path: path.join(__dirname, 'fixtures', 'moments-mobile.png') })

  // ---------- 收尾 ----------
  sql(`delete from album_items where album_id = '${albumId}'; delete from albums where id = '${albumId}'`)
  const logs = execSync(`${COMPOSE} logs api --since "${startedAt}" --no-color`).toString()
  const errors = logs.split('\n').filter((l) => / (ERR|FTL)\]|fail:/.test(l))
  check('api 日志里没有错误', errors.length === 0, errors.slice(0, 3))

  await browser.close()
  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch((e) => {
  console.error(e)
  process.exit(2)
})
