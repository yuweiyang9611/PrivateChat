// 家庭页端到端：真实 Chromium 对 compose 栈（局域网密码登录），两个浏览器（admin / bob）。
// 覆盖：成员名单；管理员邀请邮箱（角色、格式检查、已是成员）；过期后重新邀请还是同一条；撤销；
// 改名字、改角色（设为管理员要确认）、停用后对方被退回登录页且登不进来、恢复；
// 普通成员只看得到名单（没有邮箱、没有任何管理入口）；手机宽度不横向溢出；api 无错误日志。
// 前提：compose 栈已启动，浏览器已安装（npx playwright install chromium）。用法：cd web && node e2e/family.cjs
const path = require('path')
const { chromium } = require('playwright')
const { execSync } = require('child_process')

const BASE = process.env.E2E_BASE || 'http://localhost:8080'
const COMPOSE = process.env.E2E_COMPOSE || `docker compose -f "${path.resolve(__dirname, '../../deploy/docker-compose.yml')}"`
const BOB_ID = '01a1b1b0-0000-7000-8000-0000000000b0'
const results = []
function check(name, ok, detail) {
  results.push({ name, ok: !!ok })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail !== undefined ? '  — ' + JSON.stringify(detail) : ''}`)
}
const sql = (q) => execSync(`${COMPOSE} exec -T postgres psql -U privatechat -d privatechat -At -c "${q}"`).toString().trim()
const ok = (p) => p.then(() => true).catch(() => false)
/** bob 回到别的测试期望的样子 */
const resetBob = () => sql(`update users set role='Member', disabled_at=null, display_name='爸爸' where id='${BOB_ID}'`)

async function login(page, who) {
  await page.goto(BASE + '/')
  await page.locator('input[autocomplete=username]').fill(who)
  await page.locator('input[type=password]').fill('admin123')
  await page.getByRole('button', { name: '登录' }).click()
  await page.locator('input[type=password]').waitFor({ state: 'detached', timeout: 15000 })
}

const confirmBox = (page) => page.locator('.el-message-box__btns .el-button--primary').click()

;(async () => {
  const startedAt = new Date().toISOString()
  const stamp = Date.now().toString(36)
  const EMAIL = `e2e-${stamp}@example.com`

  sql(`insert into users (id, email, username, display_name, password_hash, role, elder_mode, created_at) select '${BOB_ID}', 'bob@example.com', 'bob', '爸爸', password_hash, 'Member', false, now() from users where email='admin@example.com' on conflict (id) do nothing`)
  resetBob()

  const browser = await chromium.launch()
  const opts = { viewport: { width: 1280, height: 860 }, timezoneId: 'Asia/Shanghai', locale: 'zh-CN' }
  const adminCtx = await browser.newContext(opts)
  const bobCtx = await browser.newContext(opts)
  const admin = await adminCtx.newPage()
  const bob = await bobCtx.newPage()
  for (const [who, p] of [['admin', admin], ['bob', bob]]) p.on('pageerror', (e) => console.log(`  [${who} pageerror]`, String(e).slice(0, 200)))

  const member = (page, email) => page.locator(`[data-testid=family-member][data-member-email="${email}"]`)
  const invite = (email) => admin.locator(`[data-testid=family-invite][data-invite-email="${email}"]`)
  /** 某个成员的"更多"菜单（挂在 body 上，靠 data-menu-for 认是谁的） */
  const menuOf = (email) => admin.locator(`[data-menu-for="${email}"]`)
  /** 打开某个成员的"更多"菜单并点其中一项 */
  async function menu(email, item) {
    await member(admin, email).locator('[data-testid=member-more]').click()
    await menuOf(email).locator(`[data-testid=${item}]`).click()
  }

  try {
    // ---- 管理员看到的家庭页 ----
    await login(admin, 'admin@example.com')
    await admin.goto(BASE + '/family')
    await member(admin, 'bob@example.com').waitFor({ timeout: 15000 })
    const adminRow = await member(admin, 'admin@example.com').innerText()
    const bobRow = await member(admin, 'bob@example.com').innerText()
    check('成员名单：自己标"我"、角色、邮箱（管理员看得到邮箱）', adminRow.includes('我') && adminRow.includes('管理员') && bobRow.includes('爸爸') && bobRow.includes('成员') && bobRow.includes('bob@example.com'), { adminRow, bobRow })

    // ---- 邀请 ----
    await admin.locator('[data-testid=invite-open]').click()
    await admin.locator('[data-testid=invite-email]').fill('不是邮箱')
    const blocked = await admin.locator('[data-testid=invite-submit]').isDisabled()
    await admin.locator('[data-testid=invite-email]').fill('BOB@example.com')
    await admin.locator('[data-testid=invite-submit]').click()
    const memberError = await admin.locator('[data-testid=invite-error]').innerText({ timeout: 10000 }).catch(() => '')
    check('邀请：格式不对点不了，已是成员的邮箱给出说明', blocked && memberError.includes('已是成员'), { blocked, memberError })

    await admin.locator('[data-testid=invite-email]').fill(`  ${EMAIL.toUpperCase()} `)
    await admin.locator('[data-testid=invite-role-Child]').click()
    await admin.locator('[data-testid=invite-submit]').click()
    await invite(EMAIL).waitFor({ timeout: 10000 })
    const closed = await ok(admin.locator('[data-testid=invite-email]').waitFor({ state: 'hidden', timeout: 5000 }))
    const invited = await invite(EMAIL).innerText()
    check('邀请成功：对话框关闭，"等待加入"里出现这个邮箱（小写）、角色和有效期', closed && invited.includes(EMAIL) && invited.includes('孩子') && invited.includes('30 天后过期'), { closed, invited })

    // ---- 过期后重新邀请：续的是同一条 ----
    sql(`update invites set expires_at = now() - interval '1 day' where email='${EMAIL}'`)
    await admin.reload()
    await invite(EMAIL).waitFor({ timeout: 15000 })
    const expiredText = await invite(EMAIL).locator('[data-testid=invite-expiry]').innerText()
    await invite(EMAIL).locator('[data-testid=invite-renew]').click()
    const renewed = await ok(admin.waitForFunction((email) => document.querySelector(`[data-invite-email="${email}"] [data-testid=invite-expiry]`)?.textContent?.includes('30 天后过期'), EMAIL, { timeout: 10000 }))
    const rows = sql(`select count(*) from invites where email='${EMAIL}'`)
    check('过期的邀请标出来；"重新邀请"续的是同一条，不会多出一行', expiredText.includes('已过期') && renewed && rows === '1' && (await invite(EMAIL).count()) === 1, { expiredText, renewed, rows })

    // ---- 撤销 ----
    await invite(EMAIL).locator('[data-testid=invite-revoke]').click()
    await confirmBox(admin)
    const gone = await ok(invite(EMAIL).waitFor({ state: 'detached', timeout: 10000 }))
    check('撤销：确认后从列表消失，库里也没了', gone && sql(`select count(*) from invites where email='${EMAIL}'`) === '0')

    // ---- 自己这一行：只能改名字 ----
    await member(admin, 'admin@example.com').locator('[data-testid=member-more]').click()
    const own = menuOf('admin@example.com')
    await own.locator('[data-testid=member-rename]').waitFor()
    const ownItems = { roles: await own.locator('[data-testid^=member-role-]').count(), disable: await own.locator('[data-testid=member-disable]').count() }
    check('自己的角色和停用不能自己改（菜单里没有）', ownItems.roles === 0 && ownItems.disable === 0, ownItems)
    // 点别处收起菜单，等它收完（它展开时正好盖住下一行的"更多"按钮）
    await admin.locator('[data-testid=family-page] h1').click()
    await own.locator('[data-testid=member-rename]').waitFor({ state: 'hidden' })

    // ---- 改名字 ----
    await menu('bob@example.com', 'member-rename')
    await admin.locator('.el-message-box__input input').fill(`  老爸${stamp}  `)
    await confirmBox(admin)
    const renamedOk = await ok(admin.waitForFunction((name) => document.querySelector('[data-member-email="bob@example.com"] [data-testid=member-name]')?.textContent === name, `老爸${stamp}`, { timeout: 10000 }))
    check('改名字：名单里立刻更新（去掉首尾空白）', renamedOk && sql(`select display_name from users where id='${BOB_ID}'`) === `老爸${stamp}`)

    // ---- 改角色 ----
    const roleTag = () => member(admin, 'bob@example.com').locator('[data-testid=member-role]').innerText()
    const waitRole = (text) => ok(admin.waitForFunction((t) => document.querySelector('[data-member-email="bob@example.com"] [data-testid=member-role]')?.textContent?.trim() === t, text, { timeout: 10000 }))
    await menu('bob@example.com', 'member-role-Child')
    const toChild = await waitRole('孩子')
    check('设为孩子：直接生效', toChild && sql(`select role from users where id='${BOB_ID}'`) === 'Child', await roleTag())

    await menu('bob@example.com', 'member-role-Admin')
    const asked = await ok(admin.locator('.el-message-box').waitFor({ timeout: 5000 }))
    const stillChild = sql(`select role from users where id='${BOB_ID}'`) === 'Child'
    await confirmBox(admin)
    const toAdmin = await waitRole('管理员')
    check('设为管理员：先确认，确认后才生效', asked && stillChild && toAdmin && sql(`select role from users where id='${BOB_ID}'`) === 'Admin', { asked, stillChild, toAdmin })

    await menu('bob@example.com', 'member-role-Member')
    check('设回成员', (await waitRole('成员')) && sql(`select role from users where id='${BOB_ID}'`) === 'Member')

    // ---- 普通成员看到的家庭页 ----
    await login(bob, 'bob')
    await bob.goto(BASE + '/family')
    await member(bob, 'bob@example.com').waitFor({ timeout: 15000 })
    const bobView = {
      text: await bob.locator('[data-testid=family-page]').innerText(),
      inviteButton: await bob.locator('[data-testid=invite-open]').count(),
      more: await bob.locator('[data-testid=member-more]').count(),
      invites: await bob.locator('[data-testid=family-invites], [data-testid=family-invites-empty]').count(),
    }
    check('普通成员：只有名单，没有邮箱、邀请入口和管理菜单', bobView.text.includes(`老爸${stamp}`) && !bobView.text.includes('@example.com') && bobView.inviteButton === 0 && bobView.more === 0 && bobView.invites === 0, bobView)

    // ---- 停用 / 恢复 ----
    await menu('bob@example.com', 'member-disable')
    await confirmBox(admin)
    const tagged = await ok(member(admin, 'bob@example.com').locator('[data-testid=member-disabled]').waitFor({ timeout: 10000 }))
    // bob 还开着页面、什么都没点：服务器掐断他的实时连接，页面重连时发现进不来了，退回登录页
    const kicked = await ok(bob.locator('input[type=password]').waitFor({ timeout: 20000 }))
    await bob.locator('input[autocomplete=username]').fill('bob')
    await bob.locator('input[type=password]').fill('admin123')
    await bob.getByRole('button', { name: '登录' }).click()
    const refused = await ok(bob.locator('.el-alert--error').waitFor({ timeout: 10000 }))
    check('停用：名单里标"已停用"，对方被退回登录页，而且登不进来', tagged && kicked && refused, { tagged, kicked, refused })

    await menu('bob@example.com', 'member-enable')
    const untagged = await ok(member(admin, 'bob@example.com').locator('[data-testid=member-disabled]').waitFor({ state: 'detached', timeout: 10000 }))
    const back = await ok(login(bob, 'bob'))
    check('恢复：标记消失，对方又能登录', untagged && back, { untagged, back })

    // ---- 手机宽度 ----
    await admin.locator('[data-testid=invite-open]').click()
    await admin.locator('[data-testid=invite-email]').fill(EMAIL)
    await admin.locator('[data-testid=invite-submit]').click()
    await invite(EMAIL).waitFor({ timeout: 10000 })
    await admin.setViewportSize({ width: 375, height: 760 })
    await admin.goto(BASE + '/family')
    await invite(EMAIL).waitFor({ timeout: 15000 })
    check('手机宽度不横向溢出', await admin.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1))
    await admin.locator('[data-testid=invite-open]').click()
    await admin.locator('[data-testid=invite-email]').waitFor()
    check('手机宽度：邀请对话框不超出屏幕', await admin.evaluate(() => {
      const r = document.querySelector('.el-dialog').getBoundingClientRect()
      return r.left >= 0 && r.right <= innerWidth + 1
    }))

    const errors = execSync(`${COMPOSE} logs api --since ${startedAt}`, { maxBuffer: 256 * 1024 * 1024 }).toString().split('\n').filter((l) => / (ERR|FTL)\]|fail:/.test(l))
    check('api 日志没有错误', errors.length === 0, errors.slice(0, 3))
  } finally {
    resetBob()
    sql(`delete from invites where email='${EMAIL}'`)
    await browser.close()
  }

  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch((e) => {
  console.error(e)
  process.exit(2)
})
