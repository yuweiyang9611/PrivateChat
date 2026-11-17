// 本机的 TLS 1.3 入口，转发到 compose 栈：给被控端"程序自带的 TLS 1.3"的端到端测试用（remote.cjs --agent-tls13）。
// 只收 TLS 1.3（像只收 1.3 的 Cloudflare 区域那样，用 1.2 来握手会被拒绝）；证书是临时自签的，
// Agent 靠 PRIVATECHAT_AGENT_TEST_CA 只信任它。记下每个请求 / WebSocket 升级用的 TLS 版本，测试据此断言。
// 需要 openssl 命令（Git for Windows 自带）。
const fs = require('fs')
const os = require('os')
const path = require('path')
const net = require('net')
const http = require('http')
const https = require('https')
const { execSync } = require('child_process')

function start(target) {
  const upstream = new URL(target)
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pc-tls13-'))
  const key = path.join(dir, 'key.pem')
  const cert = path.join(dir, 'cert.pem')
  execSync(`openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -keyout "${key}" -out "${cert}" -days 2 -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost"`,
    { stdio: 'ignore', env: { ...process.env, MSYS_NO_PATHCONV: '1', MSYS2_ARG_CONV_EXCL: '*' } })

  const seen = { requests: [], upgrades: [] }
  const server = https.createServer({ key: fs.readFileSync(key), cert: fs.readFileSync(cert), minVersion: 'TLSv1.3' }, (req, res) => {
    seen.requests.push({ path: req.url.split('?')[0], tls: req.socket.getProtocol(), host: req.headers.host })
    const up = http.request({ host: upstream.hostname, port: upstream.port || 80, method: req.method, path: req.url, headers: req.headers }, (r) => {
      res.writeHead(r.statusCode, r.headers)
      r.pipe(res)
    })
    up.on('error', () => { res.writeHead(502); res.end() })
    req.pipe(up)
  })
  server.on('upgrade', (req, socket, head) => {
    seen.upgrades.push({ path: req.url.split('?')[0], tls: socket.getProtocol(), host: req.headers.host })
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

  return new Promise((resolve, reject) => {
    server.on('error', reject)
    server.listen(0, '127.0.0.1', () => resolve({
      url: `https://localhost:${server.address().port}`,
      port: server.address().port,
      cert,
      seen,
      close() {
        server.closeAllConnections?.()
        server.close()
        try { fs.rmSync(dir, { recursive: true, force: true }) } catch { /* 还占着 */ }
      },
    }))
  })
}

module.exports = { start }
