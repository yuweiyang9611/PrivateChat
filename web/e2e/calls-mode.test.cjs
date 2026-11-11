// 通过 CLI 入口验证模式与安全门。Docker / 浏览器 / 网络由外部边界替身接管。
const { test } = require('node:test')
const assert = require('node:assert/strict')
const path = require('node:path')
const { spawnSync } = require('node:child_process')

function run({ runtime = {}, activeCalls = 0, recreateFails = false, flag = '1', base = 'http://localhost:8080' } = {}) {
  const result = spawnSync(process.execPath, [
    '--require', path.join(__dirname, 'fixtures/calls-cli-probe.cjs'),
    path.join(__dirname, 'calls.cjs'),
  ], {
    encoding: 'utf8', timeout: 10000,
    env: { ...process.env, E2E_REAL_CF: flag, E2E_BASE: base, CALLS_CLI_FIXTURE: JSON.stringify({ runtime, activeCalls, recreateFails }) },
  })
  if (result.error) throw result.error
  return { status: result.status, output: result.stdout + result.stderr }
}

const configured = {
  CloudflareRealtime__AppId: 'fixture-app',
  CloudflareRealtime__AppSecret: 'DO_NOT_LOG_APP_SECRET',
  CloudflareRealtime__TurnKeyId: 'fixture-turn',
  CloudflareRealtime__TurnApiToken: 'DO_NOT_LOG_TURN_TOKEN',
  CloudflareRealtime__BaseUrl: 'https://rtc.live.cloudflare.com/v1',
}

test('真实模式即使浏览器启动失败，也不替换或还原 api 配置', () => {
  const result = run({ runtime: configured })
  assert.equal(result.status, 1)
  assert.match(result.output, /CLI_PROBE_STOP_AT_BROWSER/)
  assert.doesNotMatch(result.output, /api-recreated|fake-sfu/)
  assert.doesNotMatch(result.output, /DO_NOT_LOG/)
})

for (const base of ['http://localhost:8788/v1', 'https://rtc.live.cloudflare.com.evil.test/v1']) {
  test(`真实模式拒绝非官方 SFU 地址 ${base}`, () => {
    const result = run({ runtime: { ...configured, CloudflareRealtime__BaseUrl: base } })
    assert.equal(result.status, 1)
    assert.match(result.output, /官方地址/)
    assert.doesNotMatch(result.output, /\[effect\]|DO_NOT_LOG/)
  })
}

test('真实模式拒绝遗留的假 SFU 凭据', () => {
  const result = run({ runtime: { ...configured, CloudflareRealtime__AppSecret: 'e2e-secret' } })
  assert.equal(result.status, 1)
  assert.match(result.output, /测试凭据/)
  assert.doesNotMatch(result.output, /\[effect\]/)
})

test('真实模式不强制清掉已有通话', () => {
  const result = run({ runtime: configured, activeCalls: 1 })
  assert.equal(result.status, 1)
  assert.match(result.output, /先挂断/)
  assert.doesNotMatch(result.output, /\[effect\]/)
})

test('模式拼写错误不能静默切回假模式', () => {
  const result = run({ flag: 'true' })
  assert.equal(result.status, 1)
  assert.match(result.output, /只接受 0 或 1/)
  assert.doesNotMatch(result.output, /\[effect\]/)
})

test('不允许把本地数据库测试指向线上网页', () => {
  const result = run({ base: 'https://chat.example.com' })
  assert.equal(result.status, 1)
  assert.match(result.output, /本地 compose 测试库/)
  assert.doesNotMatch(result.output, /\[effect\]/)
})

test('真实模式缺配置时，在修改数据库、启动浏览器或替换 api 之前退出', () => {
  const result = run()
  assert.equal(result.status, 1)
  assert.match(result.output, /CF_REALTIME_APP_ID/)
  assert.doesNotMatch(result.output, /\[effect\]/)
})

test('真实模式单独缺 TURN token 也在任何写操作前退出', () => {
  const result = run({ runtime: { ...configured, CloudflareRealtime__TurnApiToken: '' } })
  assert.equal(result.status, 1)
  assert.match(result.output, /CF_TURN_KEY_API_TOKEN/)
  assert.doesNotMatch(result.output, /\[effect\]|DO_NOT_LOG/)
})

for (const flag of ['', '0']) {
  test(`假模式 ${JSON.stringify(flag)} 在浏览器启动失败后关闭假 SFU 并还原 api`, () => {
    const result = run({ flag })
    assert.equal(result.status, 1)
    assert.match(result.output, /CLI_PROBE_STOP_AT_BROWSER/)
    assert.match(result.output, /fake-sfu-started/)
    assert.match(result.output, /fake-sfu-closed/)
    assert.equal((result.output.match(/api-recreated/g) || []).length, 2)
  })
}

test('假模式重建 api 中途失败仍尝试还原配置', () => {
  const result = run({ flag: '0', recreateFails: true })
  assert.equal(result.status, 1)
  assert.match(result.output, /CLI_PROBE_RECREATE_FAILED/)
  assert.match(result.output, /fake-sfu-closed/)
  assert.equal((result.output.match(/api-recreated/g) || []).length, 2)
  assert.doesNotMatch(result.output, /browser-started/)
})

for (const activeCalls of [1, 'not-a-number']) {
  test(`假模式拒绝通话状态 ${activeCalls}，不修改数据或重建 api`, () => {
    const result = run({ flag: '0', activeCalls })
    assert.equal(result.status, 1)
    assert.match(result.output, /先挂断/)
    assert.doesNotMatch(result.output, /\[effect\]/)
  })
}
