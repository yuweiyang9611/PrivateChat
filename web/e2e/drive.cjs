// 网盘端到端：真实 Chromium（headless）对 compose 栈。覆盖新建 / 上传（含 HEVC 视频与带 EXIF 的照片）/ 缩略图 /
// 720p 转码 / 相册时间线 / 改名 / 共享给家人 / 外链 / 发到聊天 / 聊天存网盘 / 移动 / 回收站。
// 前提：compose 栈已启动，浏览器已安装（npx playwright install chromium）。用法：cd web && node e2e/drive.cjs
const path = require('path')
const { chromium } = require('playwright')
const { execSync } = require('child_process')

const BASE = process.env.E2E_BASE || 'http://localhost:8080'
const COMPOSE = `docker compose -f "${path.resolve(__dirname, '../../deploy/docker-compose.yml')}"`
const VIDEO = path.join(__dirname, 'fixtures', 'hevc-2018.mp4')
const results = []
function check(name, ok, detail) {
  results.push({ name, ok: !!ok })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail !== undefined ? '  — ' + JSON.stringify(detail) : ''}`)
}
const sql = (q) => execSync(`${COMPOSE} exec -T postgres psql -U privatechat -d privatechat -At -c "${q}"`).toString().trim()

/** 在 canvas JPEG 的 SOI 之后插入只含 DateTimeOriginal 的 EXIF APP1 段 */
function withExifDate(jpeg, date) {
  const tiff = Buffer.alloc(64)
  tiff.write('II', 0, 'ascii'); tiff.writeUInt16LE(42, 2); tiff.writeUInt32LE(8, 4)
  tiff.writeUInt16LE(1, 8) // IFD0：1 项
  tiff.writeUInt16LE(0x8769, 10); tiff.writeUInt16LE(4, 12); tiff.writeUInt32LE(1, 14); tiff.writeUInt32LE(26, 18) // ExifIFD 指针
  tiff.writeUInt32LE(0, 22)
  tiff.writeUInt16LE(1, 26) // ExifIFD：1 项
  tiff.writeUInt16LE(0x9003, 28); tiff.writeUInt16LE(2, 30); tiff.writeUInt32LE(20, 32); tiff.writeUInt32LE(44, 36) // DateTimeOriginal
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
  // 登录页消失即可（新成员可能一个会话都没有）
  await page.locator('input[type=password]').waitFor({ state: 'detached', timeout: 15000 })
}

async function menu(page, nodeName, item) {
  const tile = page.locator(`[data-node-name="${nodeName}"]`).first()
  await tile.hover()
  await tile.locator('[data-testid=node-menu]').click()
  await page.locator('.el-dropdown-menu__item:visible', { hasText: item }).first().click()
}

async function promptAndConfirm(page, value) {
  const input = page.locator('.el-message-box__input input')
  await input.fill(value)
  await page.locator('.el-message-box__btns .el-button--primary').click()
}

;(async () => {
  const stamp = Date.now()
  const folder = `春游-${stamp}`
  const browser = await chromium.launch()
  const ctx = await browser.newContext({ viewport: { width: 1200, height: 860 }, timezoneId: 'Asia/Shanghai', locale: 'zh-CN' })
  const page = await ctx.newPage()
  page.on('pageerror', (e) => console.log('  [pageerror]', String(e).slice(0, 200)))
  await login(page, 'admin@example.com')

  // ---------- 新建文件夹、进入 ----------
  await page.goto(BASE + '/drive')
  await page.waitForURL('**/drive/my')
  await page.click('[data-testid=drive-new-folder]')
  await promptAndConfirm(page, folder)
  await page.locator(`[data-node-name="${folder}"]`).first().waitFor({ timeout: 10000 })
  check('新建文件夹出现在"我的文件"', true)
  await page.locator(`[data-node-name="${folder}"] button`).first().click()
  await page.waitForURL(/\/drive\/my\/[0-9a-f-]{36}$/)
  // 目录加载完（面包屑出现文件夹名、可写时出现上传按钮）再操作
  await page.waitForFunction((f) => document.querySelector('[data-testid=drive-crumbs]')?.textContent?.includes(f), folder, { timeout: 10000 })
  await page.locator('[data-testid=drive-upload]').waitFor()
  const crumbs = await page.locator('[data-testid=drive-crumbs]').innerText()
  check('面包屑显示路径', crumbs.includes('我的文件') && crumbs.includes(folder), crumbs)

  // ---------- 上传：带 EXIF 的照片 + HEVC 视频 ----------
  const jpegB64 = await page.evaluate(() => {
    const c = document.createElement('canvas'); c.width = 1600; c.height = 1200
    const g = c.getContext('2d'); const grd = g.createLinearGradient(0, 0, 1600, 1200)
    grd.addColorStop(0, '#2a9d8f'); grd.addColorStop(1, '#e9c46a'); g.fillStyle = grd; g.fillRect(0, 0, 1600, 1200)
    g.fillStyle = '#fff'; g.font = '120px sans-serif'; g.fillText('2019 春游 ' + Math.random().toString(36).slice(2, 6), 100, 650)
    return c.toDataURL('image/jpeg', 0.9).split(',')[1]
  })
  const jpeg = withExifDate(Buffer.from(jpegB64, 'base64'), '2019:05:01 10:00:00')
  await page.setInputFiles('[data-testid=drive-file-input]', [
    { name: '公园.jpg', mimeType: 'image/jpeg', buffer: jpeg },
    { name: 'hevc.mp4', mimeType: 'video/mp4', buffer: require('fs').readFileSync(VIDEO) },
  ])
  const uploaded = await page.waitForFunction(() => {
    const q = document.querySelector('[data-testid=upload-queue]')
    const items = [...(q?.querySelectorAll('[data-upload-status]') ?? [])].map((e) => e.getAttribute('data-upload-status'))
    return items.length === 2 && items.every((s) => s === 'done')
  }, null, { timeout: 60000 }).then(() => true).catch(() => false)
  check('两个文件上传完成（上传队列）', uploaded)
  await page.locator('[data-node-name="公园.jpg"]').first().waitFor({ timeout: 10000 })
  await page.locator('[data-node-name="hevc.mp4"]').first().waitFor({ timeout: 10000 })
  check('上传的文件出现在当前文件夹', true)

  // ---------- 缩略图（视频抽帧）与 720p 转码 ----------
  const thumbs = await page.waitForFunction(() => {
    const ok = (name) => { const i = document.querySelector(`[data-node-name="${name}"] img`); return i && i.complete && i.naturalWidth > 0 }
    return ok('公园.jpg') && ok('hevc.mp4')
  }, null, { timeout: 90000, polling: 1000 }).then(() => true).catch(() => false)
  check('照片与视频的缩略图都生成并显示（实时刷新）', thumbs)
  const videoBlob = sql(`select blob_id from drive_nodes where name = 'hevc.mp4' and deleted_at is null order by created_at desc limit 1`)
  let play = ''
  for (let i = 0; i < 60 && !/Ready/.test(play); i++) {
    play = sql(`select status || ' ' || coalesce(storage_key, '') from derived_blobs where blob_id = '${videoBlob}' and variant = 'play-720p'`)
    if (!/Ready/.test(play)) await page.waitForTimeout(1000)
  }
  const key = play.split(' ')[1]
  const codec = key ? execSync(`${COMPOSE} exec -T api ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,width,height -of csv=p=0 /data/derived/${key}`).toString().trim() : ''
  check('HEVC 视频转成 H.264 720p', /Ready/.test(play) && codec.startsWith('h264,1280,720'), { play, codec })
  const media = sql(`select b.width || 'x' || b.height || ' ' || to_char(b.taken_at at time zone 'Asia/Shanghai', 'YYYY-MM-DD HH24:MI') from blobs b join drive_nodes n on n.blob_id = b.id where n.name in ('公园.jpg', 'hevc.mp4') and n.deleted_at is null order by n.name`)
  check('媒体信息：照片 EXIF 拍摄时间、视频尺寸与创建时间', media.includes('1600x1200 2019-05-01 10:00') && media.includes('1280x720 2018-07-04 20:00'), media)

  // ---------- 相册时间线 ----------
  await page.click('[data-testid=drive-nav-gallery]')
  await page.waitForSelector('[data-testid=gallery] section')
  const jpgBlob = sql(`select blob_id from drive_nodes where name = '公园.jpg' and deleted_at is null order by created_at desc limit 1`)
  for (let i = 0; i < 20; i++) {
    const found = await page.evaluate((id) => !!document.querySelector(`[data-blob="${id}"]`), jpgBlob)
    if (found) break
    await page.mouse.wheel(0, 4000); await page.waitForTimeout(400)
  }
  const groupOf = (id) => page.evaluate((b) => document.querySelector(`[data-blob="${b}"]`)?.closest('section')?.querySelector('h3')?.textContent ?? null, id)
  const g1 = await groupOf(jpgBlob)
  const g2 = await groupOf(videoBlob)
  check('相册按拍摄时间分到 2019 年 5 月 / 2018 年 7 月', g1 === '2019 年 5 月' && g2 === '2018 年 7 月', { g1, g2 })
  await page.click(`[data-blob="${videoBlob}"] button`)
  const playing = await page.waitForFunction(() => { const v = document.querySelector('[data-testid=media-dialog] video'); return v && v.readyState >= 2 && v.videoWidth > 0 ? `${v.videoWidth}x${v.videoHeight}` : false }, null, { timeout: 15000 }).then((h) => h.jsonValue()).catch(() => null)
  check('相册里点视频，播放的是 720p 版本', playing === '1280x720', playing)
  await page.keyboard.press('Escape')

  // ---------- 改名 ----------
  const folderId = sql(`select id from drive_nodes where name = '${folder}' and deleted_at is null`)
  await page.goto(BASE + `/drive/my/${folderId}`)
  await page.locator('[data-node-name="公园.jpg"]').first().waitFor()
  await menu(page, '公园.jpg', '重命名')
  await promptAndConfirm(page, '公园合影.jpg')
  const renamed = await page.locator('[data-node-name="公园合影.jpg"]').first().waitFor({ timeout: 8000 }).then(() => true).catch(() => false)
  check('重命名生效', renamed)

  // ---------- 共享文件夹给家人（只读） + 外链 ----------
  await page.goto(BASE + '/drive/my')
  await page.locator(`[data-node-name="${folder}"]`).first().waitFor()
  await menu(page, folder, '共享')
  await page.waitForSelector('[data-testid=share-dialog]')
  await page.click('[data-testid=share-member]')
  await page.locator('.el-select-dropdown__item:visible').first().click()
  await page.click('[data-testid=share-add]')
  await page.waitForFunction(() => document.querySelector('[data-testid=share-dialog]')?.innerText.includes('可查看'), null, { timeout: 8000 })
  await page.click('[data-testid=share-create-link]')
  const linkUrl = await page.locator('[data-testid=share-link-url]').first().innerText({ timeout: 8000 })
  check('生成外链', /\/s\/[A-Za-z0-9_-]{40,}$/.test(linkUrl), linkUrl)
  await page.keyboard.press('Escape')

  const bobCtx = await browser.newContext({ viewport: { width: 1200, height: 860 } })
  const bob = await bobCtx.newPage()
  await login(bob, 'bob')
  await bob.goto(BASE + '/drive/shared')
  await bob.locator(`[data-node-name="${folder}"]`).first().click({ timeout: 10000 })
  await bob.locator('[data-node-name="公园合影.jpg"]').first().waitFor({ timeout: 10000 })
  const bobCanUpload = await bob.locator('[data-testid=drive-upload]').count()
  check('家人在"与我共享"里打开文件夹、只读（没有上传按钮）', bobCanUpload === 0)
  await bobCtx.close()

  const anonCtx = await browser.newContext()
  const anon = await anonCtx.newPage()
  const token = linkUrl.split('/s/')[1]
  await anon.goto(`${BASE}/s/${token}`)
  const pageText = await anon.locator('body').innerText()
  const dl = await anon.request.get(`${BASE}/s/${token}/f/${sql(`select id from drive_nodes where name = '公园合影.jpg' and deleted_at is null order by created_at desc limit 1`)}?download=true`)
  check('外链页（未登录）列出文件并能下载', pageText.includes('公园合影.jpg') && dl.status() === 200 && (await dl.body()).length === jpeg.length, { status: dl.status() })
  await anonCtx.close()

  // ---------- 发到聊天 → 视频带封面播放 720p；再从聊天存回网盘 ----------
  await page.goto(BASE + `/drive/my/${sql(`select id from drive_nodes where name = '${folder}' and deleted_at is null`)}`)
  await page.locator('[data-node-name="hevc.mp4"]').first().waitFor()
  await menu(page, 'hevc.mp4', '发送到聊天')
  await page.waitForSelector('[data-testid=send-dialog]')
  await page.locator('[data-testid=send-dialog] ul button').first().click()
  await page.click('[data-testid=send-and-open]')
  await page.waitForURL(/\/messages\/[0-9a-f-]{36}$/)
  const chatVideo = await page.waitForFunction((b) => {
    document.querySelector('[data-testid=message-scroller]')?.dispatchEvent(new Event('scroll'))
    const v = [...document.querySelectorAll('video')].find((x) => x.getAttribute('src')?.includes(b))
    return v ? { src: v.getAttribute('src'), poster: v.getAttribute('poster') } : false
  }, videoBlob, { timeout: 15000 }).then((h) => h.jsonValue()).catch(() => null)
  check('发到聊天：视频消息带封面、播放 720p', !!chatVideo && chatVideo.src.includes('play-720p') && (chatVideo.poster || '').includes('thumb-1280'), chatVideo)
  const rows = page.locator('[data-seq]')
  const last = rows.last()
  await last.hover()
  await last.locator('[data-testid=msg-actions]').click()
  await page.locator('[data-testid=save-to-drive]:visible').first().click()
  await page.waitForTimeout(1000)
  const savedCount = Number(sql(`select count(*) from drive_nodes n join spaces s on s.id = n.space_id where n.blob_id = '${videoBlob}' and n.parent_id is null and s.type = 'Personal' and n.deleted_at is null`))
  check('聊天里的视频"保存到网盘"落到我的文件根目录', savedCount >= 1, savedCount)

  // ---------- 移动到家庭共享 ----------
  await page.goto(BASE + `/drive/my/${sql(`select id from drive_nodes where name = '${folder}' and deleted_at is null`)}`)
  await page.locator('[data-node-name="公园合影.jpg"]').first().waitFor()
  await menu(page, '公园合影.jpg', '移动到')
  await page.waitForSelector('[data-testid=move-dialog]')
  await page.locator('[data-testid=move-dialog] .el-radio-button', { hasText: '家庭共享' }).click()
  await page.locator('[data-testid=move-dialog] .el-tree-node__content', { hasText: '根目录' }).first().click()
  await page.click('[data-testid=move-confirm]')
  await page.waitForTimeout(800)
  const inFamily = sql(`select s.type from drive_nodes n join spaces s on s.id = n.space_id where n.name like '公园合影%' and n.deleted_at is null order by n.updated_at desc limit 1`)
  check('移动到家庭共享根目录', inFamily === 'Family', inFamily)

  // ---------- 删除 → 回收站 → 还原 ----------
  await page.goto(BASE + '/drive/my')
  await page.locator(`[data-node-name="${folder}"]`).first().waitFor()
  await menu(page, folder, '删除')
  await page.locator('.el-message-box__btns .el-button--primary').click()
  await page.locator(`[data-node-name="${folder}"]`).first().waitFor({ state: 'detached', timeout: 8000 })
  await page.click('[data-testid=drive-nav-trash]')
  await page.locator(`[data-testid=trash] [data-node-name="${folder}"]`).waitFor({ timeout: 8000 })
  await page.locator(`[data-testid=trash] [data-node-name="${folder}"] [data-testid=trash-restore]`).click()
  await page.locator(`[data-testid=trash] [data-node-name="${folder}"]`).waitFor({ state: 'detached', timeout: 8000 })
  await page.click('[data-testid=drive-nav-my]')
  const back = await page.locator(`[data-node-name="${folder}"]`).first().waitFor({ timeout: 8000 }).then(() => true).catch(() => false)
  check('删除进回收站、还原后回到原处', back)

  // ---------- 手机布局 ----------
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto(BASE + '/drive/my')
  await page.locator(`[data-node-name="${folder}"]`).first().waitFor()
  const mobileNav = await page.evaluate(() => {
    // 网盘分区横条（不是面包屑）：在手机宽度下可见
    const bar = [...document.querySelectorAll('nav')].find((n) => n.offsetParent !== null && n.getAttribute('data-testid') !== 'drive-crumbs' && n.textContent.includes('回收站'))
    return bar ? [...bar.querySelectorAll('a')].map((a) => a.textContent.trim()).filter((t) => ['我的文件', '家庭共享', '相册', '与我共享', '回收站'].includes(t)).length : 0
  })
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1)
  check('手机宽度：顶部分区导航可用、无横向溢出', mobileNav === 5 && !overflow, { mobileNav, overflow })
  await page.screenshot({ path: path.join(__dirname, 'fixtures', 'drive-mobile.png') })

  await browser.close()
  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch((e) => {
  console.error(e)
  process.exit(2)
})
