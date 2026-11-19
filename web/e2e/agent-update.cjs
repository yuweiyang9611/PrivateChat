// 被控端自动升级端到端：用真正发布出来的安装包（web 镜像里的 /downloads/PrivateChat.Agent.exe），不开浏览器。
// 把安装包下到临时目录当作"装在电脑上的程序"，让它假装自己是旧版本（--pretend-version 0.0.1），然后：
//   1. 清单里的 SHA-256 被改过：不升级，照常在线，程序文件原样；
//   2. 新版本根本启动不了（清单和文件对得上，但文件不是能运行的程序）：换回原来的，照常在线；
//   3. 正常情况：自己发现新版本 → 下载 → 核对 → 换掉自己 → 重启，重新上线后报的是新版本号，旧文件清掉，只剩一个进程，不会反复升级。
// 被控端连的是本脚本起的一个转发入口（好在中途改清单 / 换文件），其余请求原样转给 compose 栈。
// 前提：compose 栈已启动，而且 web 镜像是用当前代码构建的。用法：cd web && node e2e/agent-update.cjs
const fs = require('fs')
const os = require('os')
const net = require('net')
const http = require('http')
const path = require('path')
const crypto = require('crypto')
const { spawn, execSync, execFileSync } = require('child_process')

const BASE = process.env.E2E_BASE || 'http://localhost:8080'
const ROOT = path.resolve(__dirname, '../..')
const COMPOSE = process.env.E2E_COMPOSE || `docker compose -f "${path.resolve(ROOT, 'deploy/docker-compose.yml')}"`
const results = []
function check(name, ok, detail) {
  results.push({ name, ok: !!ok })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail !== undefined ? '  — ' + JSON.stringify(detail) : ''}`)
}
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
const sha256 = (file) => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex')

let cookie = ''
async function api(method, url, body) {
  const r = await fetch(BASE + url, { method, headers: { ...(body ? { 'Content-Type': 'application/json' } : {}), ...(cookie ? { Cookie: cookie } : {}) }, body: body ? JSON.stringify(body) : undefined })
  const set = r.headers.getSetCookie?.() ?? []
  if (set.length) cookie = set.map((c) => c.split(';')[0]).join('; ')
  const text = await r.text()
  if (!r.ok) throw new Error(`${method} ${url} → ${r.status} ${text.slice(0, 200)}`)
  try { return text ? JSON.parse(text) : null } catch { return null }
}

/** 转发入口：mode = pass 原样转发；tamper 把清单里的 SHA-256 改掉；bad-exe 发一个"对得上清单、但不是程序"的文件 */
function startEntry() {
  const upstream = new URL(BASE)
  const garbage = crypto.randomBytes(4096)
  const state = { mode: 'pass', manifestHits: 0, installerHits: 0 }
  const server = http.createServer((req, res) => {
    const p = req.url.split('?')[0]
    if (p === '/downloads/PrivateChat.Agent.json') state.manifestHits++
    if (p === '/downloads/PrivateChat.Agent.exe') state.installerHits++
    if (state.mode === 'bad-exe' && p === '/downloads/PrivateChat.Agent.json') {
      res.writeHead(200, { 'Content-Type': 'application/json' })
      return res.end(JSON.stringify({ version: '99.0.0', sha256: crypto.createHash('sha256').update(garbage).digest('hex'), size: garbage.length }))
    }
    if (state.mode === 'bad-exe' && p === '/downloads/PrivateChat.Agent.exe') {
      res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': garbage.length })
      return res.end(garbage)
    }
    const up = http.request({ host: upstream.hostname, port: upstream.port || 80, method: req.method, path: req.url, headers: req.headers }, (r) => {
      if (state.mode === 'tamper' && p === '/downloads/PrivateChat.Agent.json') {
        let body = ''
        r.on('data', (d) => { body += d })
        r.on('end', () => {
          const m = JSON.parse(body)
          m.sha256 = m.sha256.replace(/^./, (c) => (c === '0' ? '1' : '0'))
          const out = JSON.stringify(m)
          res.writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(out) })
          res.end(out)
        })
        return
      }
      res.writeHead(r.statusCode, r.headers)
      r.pipe(res)
    })
    up.on('error', () => { res.writeHead(502); res.end() })
    req.pipe(up)
  })
  server.on('upgrade', (req, socket, head) => {
    const up = net.connect(Number(upstream.port || 80), upstream.hostname, () => {
      const lines = [`${req.method} ${req.url} HTTP/1.1`]
      for (let i = 0; i < req.rawHeaders.length; i += 2) lines.push(`${req.rawHeaders[i]}: ${req.rawHeaders[i + 1]}`)
      up.write(lines.join('\r\n') + '\r\n\r\n')
      if (head.length) up.write(head)
      up.pipe(socket)
      socket.pipe(up)
    })
    up.on('error', () => socket.destroy())
    socket.on('error', () => up.destroy())
  })
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve({
    url: `http://127.0.0.1:${server.address().port}`,
    state,
    close() { server.closeAllConnections?.(); server.close() },
  })))
}

/** 这个数据目录上现在跑着几个被控端进程（按命令行里的数据目录认） */
function agentPids(dataDir) {
  const ps = `[Console]::OutputEncoding = [Text.Encoding]::UTF8; (Get-CimInstance Win32_Process -Filter "Name = 'PrivateChat.Agent.exe'" | Where-Object { $_.CommandLine -like '*${dataDir.replace(/'/g, "''")}*' } | ForEach-Object { $_.ProcessId }) -join ','`
  return execFileSync('powershell', ['-NoProfile', '-Command', ps], { encoding: 'utf8' }).trim().split(',').filter(Boolean).map(Number)
}
const killAgents = (dataDir) => { for (const pid of agentPids(dataDir)) try { process.kill(pid) } catch { /* 已退出 */ } }

;(async () => {
  const startedAt = new Date().toISOString()
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'pc-agent-update-'))
  const exe = path.join(work, 'app', 'PrivateChat.Agent.exe')
  const dataDir = path.join(work, 'data')
  fs.mkdirSync(path.dirname(exe), { recursive: true })
  let entry = null
  let deviceId = null
  // 被控端说的话从它的日志文件里读：升级后新进程接着往同一个文件里写（标准输出不一定还连着这个脚本）
  const logFile = path.join(dataDir, 'agent.log')
  let out = ''
  const read = () => {
    try { out = fs.readFileSync(logFile, 'utf8') } catch { /* 还没开始写 */ }
    return out
  }
  const cleanup = () => {
    try { killAgents(dataDir) } catch { /* 没有了 */ }
    try { entry?.close() } catch { /* 已关 */ }
  }
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK']) process.on(signal, () => { cleanup(); process.exit(1) })

  /** 启动被控端（无界面、测试画面、键鼠不注入） */
  const start = (extra = [], env = {}) => {
    const proc = spawn(exe, ['--headless', '--server', entry.url, '--data-dir', dataDir, '--name', '升级测试电脑', '--consent', 'ignore',
      '--source', 'pattern', '--dry-input', path.join(work, 'input.jsonl'), ...extra], { env: { ...process.env, PRIVATECHAT_AGENT_TEST: '1', ...env }, stdio: 'ignore' })
    return proc
  }
  const stopAll = async () => {
    killAgents(dataDir)
    await until(() => agentPids(dataDir).length === 0, 10000)
  }
  const saysSince = (mark, text, timeout) => until(() => read().slice(mark).includes(text), timeout, 100)
  const device = async () => (await api('GET', '/api/devices')).find((d) => d.id === deviceId)

  try {
    // ---- 发布出来的清单和安装包 ----
    const manifestRes = await fetch(BASE + '/downloads/PrivateChat.Agent.json')
    const manifest = manifestRes.ok ? await manifestRes.json() : null
    const csproj = fs.readFileSync(path.resolve(ROOT, 'src/PrivateChat.Agent/PrivateChat.Agent.csproj'), 'utf8').match(/<Version>([^<]+)<\/Version>/)?.[1]
    fs.writeFileSync(exe, Buffer.from(await (await fetch(BASE + '/downloads/PrivateChat.Agent.exe')).arrayBuffer()))
    const original = sha256(exe)
    check('镜像里有版本清单，版本号是当前代码的，大小和 SHA-256 与安装包对得上',
      !!manifest && manifest.version === csproj && manifest.sha256 === original && manifest.size === fs.statSync(exe).size && (manifestRes.headers.get('content-type') ?? '').includes('json'),
      { manifest, csproj, type: manifestRes.headers.get('content-type') })
    if (!manifest) throw new Error('没有清单：web 镜像不是用当前代码构建的')
    const VERSION = manifest.version

    // ---- 先登记一次（不升级），拿到设备令牌 ----
    entry = await startEntry()
    await api('POST', '/api/auth/local/login', { usernameOrEmail: 'admin@example.com', password: 'admin123' })
    start([], { PRIVATECHAT_AGENT_AUTOUPDATE: 'off' })
    const codeFile = path.join(dataDir, 'device-code.txt')
    const code = await until(() => (fs.existsSync(codeFile) ? fs.readFileSync(codeFile, 'utf8').trim() : null), 30000)
    deviceId = code ? (await api('POST', '/api/auth/device/confirm', { code })).deviceId : null
    const registered = await saysSince(0, '已连接服务器', 30000)
    check('发布出来的安装包能登记上线；关掉自动升级时明说没有启用', !!registered && out.includes('自动升级没有启用') && (await device())?.agentVersion === VERSION,
      { version: (await device())?.agentVersion })
    await stopAll()

    // ---- 1. 清单被改过：SHA-256 对不上，不升级 ----
    entry.state.mode = 'tamper'
    let mark = read().length
    start(['--pretend-version', '0.0.1'])
    const refused = await saysSince(mark, 'SHA-256 对不上', 60000)
    await sleep(1500)
    const afterTamper = await device()
    check('下载到的文件和清单对不上：不升级，照常在线，程序文件原样，没有留下没核对过的文件',
      !!refused && out.slice(mark).includes('自动升级没有成功') && !out.slice(mark).includes('已换成新版本')
      && afterTamper?.online && afterTamper.agentVersion === '0.0.1' && sha256(exe) === original
      && fs.readdirSync(path.dirname(exe)).length === 1 && fs.readdirSync(path.join(dataDir, 'update')).length === 0,
      { online: afterTamper?.online, version: afterTamper?.agentVersion, app: fs.readdirSync(path.dirname(exe)), update: fs.readdirSync(path.join(dataDir, 'update')) })
    await stopAll()

    // ---- 2. 新版本启动不了：换回原来的 ----
    entry.state.mode = 'bad-exe'
    mark = read().length
    start(['--pretend-version', '0.0.1'])
    const rolledBack = await saysSince(mark, '自动升级没有成功', 60000)
    await sleep(1500)
    const afterBad = await device()
    check('新版本启动不了：换回原来的程序接着跑，照常在线',
      !!rolledBack && out.slice(mark).includes('发现新版本 99.0.0') && out.slice(mark).includes('核对无误') && !out.slice(mark).includes('已换成新版本')
      && afterBad?.online && sha256(exe) === original && agentPids(dataDir).length === 1
      && !fs.readdirSync(path.dirname(exe)).some((f) => f.endsWith('.old') || f.endsWith('.bad')),
      { said: out.slice(mark).match(/自动升级没有成功[^\r\n]*/)?.[0], online: afterBad?.online, app: fs.readdirSync(path.dirname(exe)) })
    await stopAll()

    // ---- 3. 正常升级 ----
    entry.state.mode = 'pass'
    Object.assign(entry.state, { manifestHits: 0, installerHits: 0 })
    mark = read().length
    const oldProc = start(['--pretend-version', '0.0.1'])
    const oldPid = oldProc.pid
    const found = await saysSince(mark, `发现新版本 ${VERSION}（现在是 0.0.1）`, 60000)
    const verified = await saysSince(mark, '大小和 SHA-256 核对无误', 120000)
    const swapped = await saysSince(mark, `已换成新版本 ${VERSION} 并启动`, 60000)
    const restarted = await saysSince(mark, `已从 0.0.1 自动升级到 ${VERSION}`, 60000)
    check('旧版本自己发现新版本、下载、核对、换掉自己，新版本接着启动', !!found && !!verified && !!swapped && !!restarted,
      { found: !!found, verified: !!verified, swapped: !!swapped, restarted: !!restarted, tail: restarted ? undefined : out.slice(-400) })

    const online = await until(async () => { const d = await device(); return d?.online && d.agentVersion === VERSION ? d : null }, 60000, 500)
    check('升级后不用重新添加：用原来的设备令牌重新上线，服务器上看到的是新版本号', !!online, { version: (await device())?.agentVersion, online: (await device())?.online })

    const oldGone = await until(() => oldProc.exitCode !== null || !agentPids(dataDir).includes(oldPid), 20000)
    const pids = agentPids(dataDir)
    check('旧进程退出，只剩新版本一个进程', !!oldGone && pids.length === 1 && pids[0] !== oldPid, { oldPid, pids })

    const tidy = await until(() => {
      const app = fs.readdirSync(path.dirname(exe))
      const update = fs.existsSync(path.join(dataDir, 'update')) ? fs.readdirSync(path.join(dataDir, 'update')) : []
      return app.length === 1 && update.length === 0
    }, 30000, 500)
    check('程序还在原来的位置（开机自启不用改），内容是新下载的那份，改名的旧文件和下载目录都清掉了',
      !!tidy && sha256(exe) === manifest.sha256, { app: fs.readdirSync(path.dirname(exe)) })

    await sleep(12000) // 新版本启动 10 秒后自己也会查一次
    const again = read().slice(mark).split('发现新版本').length - 1
    check('升级完不会反复升级：新版本查过一次，发现已是最新', again === 1 && entry.state.installerHits === 1 && entry.state.manifestHits >= 2,
      { found: again, installerDownloads: entry.state.installerHits, manifestChecks: entry.state.manifestHits })

    const errors = execSync(`${COMPOSE} logs api --since ${startedAt}`, { maxBuffer: 256 * 1024 * 1024 }).toString().split('\n').filter((l) => /\bERR\b|Unhandled|Exception/.test(l))
    check('api 日志没有错误', errors.length === 0, errors.slice(0, 3))
  } finally {
    try { if (deviceId) await api('DELETE', `/api/devices/${deviceId}`) } catch { /* 栈没起来 */ }
    cleanup()
    await sleep(1000)
    try { fs.rmSync(work, { recursive: true, force: true }) } catch { /* 进程还占着 */ }
  }

  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch((e) => {
  console.error(e)
  process.exit(2)
})
