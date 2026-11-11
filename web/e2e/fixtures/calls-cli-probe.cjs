// CLI 安全门测试的外部边界替身：不访问 Docker、不启动浏览器、不请求网络。
// 用 node --require 加载，仅由 calls-mode.test.cjs 使用；通话测试本身不加载此文件。
const Module = require('node:module')
const originalLoad = Module._load
const fixture = JSON.parse(process.env.CALLS_CLI_FIXTURE || '{}')
let recreates = 0

function docker(command) {
  if (command.includes(' ps -q api')) return 'fixture-api\n'
  if (command.includes('inspect')) {
    return JSON.stringify(Object.entries(fixture.runtime || {}).map(([key, value]) => `${key}=${value}`))
  }
  if (command.includes('up -d')) {
    console.log('[effect] api-recreated')
    if (++recreates === 1 && fixture.recreateFails) throw new Error('CLI_PROBE_RECREATE_FAILED')
    return ''
  }
  if (/\b(insert|update|delete)\b/i.test(command)) {
    console.log('[effect] database-written')
    return ''
  }
  if (command.includes('select id from users')) return '00000000-0000-7000-8000-000000000001'
  if (command.includes('select display_name from users')) return 'admin'
  if (command.includes('call_sessions') || command.includes('call_participants')) return String(fixture.activeCalls ?? 0)
  if (command.includes('count(*)')) return '0'
  throw new Error('Unexpected Docker command in CLI probe')
}

Module._load = function (name, parent, isMain) {
  if (name === './fake-sfu.cjs') {
    console.log('[effect] fake-sfu-loaded')
    return { startFakeSfu: async () => {
      console.log('[effect] fake-sfu-started')
      return { close: async () => console.log('[effect] fake-sfu-closed') }
    } }
  }
  if (name === 'child_process' || name === 'node:child_process') {
    return {
      execSync: (command) => docker(command),
      execFileSync: (file, args) => docker([file, ...args].join(' ')),
    }
  }
  if (name === 'playwright') {
    return { chromium: { launch: async () => {
      console.log('[effect] browser-started')
      // 测到启动边界即停止：这个套件不冒充真实媒体测试。
      throw new Error('CLI_PROBE_STOP_AT_BROWSER')
    } } }
  }
  return originalLoad.call(this, name, parent, isMain)
}

globalThis.fetch = async () => ({ ok: true, status: 200 })
