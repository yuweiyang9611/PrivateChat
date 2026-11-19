// 一条"慢线路"：本机的 TCP 转发，限速 + 加延迟。弱网测试里被控端经它连服务器（e2e/remote-weak.cjs），
// 相当于被控电脑的上行带宽只有这么多、到服务器的单程延迟有这么长。
// 模型：每个方向各一个先进先出的队列，数据按 kbps 的速度"传完"之后再等 delayMs 才交给对面；
// 队列里积了超过 300 ms 的量就不再从来源读（TCP 背压一路传回发送方，和真实的瓶颈链路一样）。
// 用法：const link = await require('./slow-link.cjs').start('http://localhost:8080', { kbps: 300, delayMs: 50 })
//       → link.url（给被控端的服务器地址）、link.bytes（两个方向各转发了多少）、link.close()
const net = require('net')

function shape(source, target, { kbps, delayMs }, count) {
  const bytesPerMs = kbps ? kbps / 8 : Infinity
  const limit = kbps ? Math.max(16 * 1024, bytesPerMs * 300) : 4 * 1024 * 1024
  const queue = [] // { piece, due }，严格先进先出：一个定时器按队头的到期时间往外放，不给每块各开一个（那样到期时间取整后会乱序）
  let free = 0
  let queued = 0
  let timer = null
  let ended = false
  const pump = () => {
    timer = null
    const now = performance.now()
    while (queue.length && queue[0].due <= now) {
      const { piece } = queue.shift()
      queued -= piece.length
      count(piece.length)
      if (!target.destroyed) target.write(piece)
    }
    if (queued < limit / 2 && source.isPaused()) source.resume()
    if (queue.length) timer = setTimeout(pump, Math.max(1, Math.ceil(queue[0].due - now)))
    else if (ended && !target.destroyed) target.end()
  }
  source.on('data', (chunk) => {
    // 切成小块：大块数据也是一点一点到的，不是等整块"传完"才一起出现
    for (let offset = 0; offset < chunk.length; offset += 2048) {
      const piece = chunk.subarray(offset, Math.min(chunk.length, offset + 2048))
      const now = performance.now()
      free = Math.max(free, now) + (kbps ? piece.length / bytesPerMs : 0)
      queued += piece.length
      queue.push({ piece, due: free + delayMs })
    }
    if (queued > limit) source.pause()
    if (!timer) timer = setTimeout(pump, Math.max(1, Math.ceil(queue[0].due - performance.now())))
  })
  source.on('end', () => {
    ended = true
    if (!timer) pump()
  })
  source.on('close', () => {
    // 来源断了：对面也断（队列里没放完的不要了）
    clearTimeout(timer)
    if (!ended) target.destroy()
  })
  source.on('error', () => target.destroy())
}

/** options：kbps（0 / 不给 = 不限速）、delayMs（单程延迟） */
async function start(targetUrl, options = {}) {
  const target = new URL(targetUrl)
  const opts = { kbps: options.kbps ?? 0, delayMs: options.delayMs ?? 0 }
  const bytes = { up: 0, down: 0 }
  const sockets = new Set()
  const server = net.createServer((client) => {
    const upstream = net.connect(Number(target.port || 80), target.hostname)
    for (const s of [client, upstream]) {
      s.setNoDelay(true)
      sockets.add(s)
      s.on('close', () => sockets.delete(s))
      s.on('error', () => undefined)
    }
    shape(client, upstream, opts, (n) => { bytes.up += n })
    shape(upstream, client, opts, (n) => { bytes.down += n })
  })
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  return {
    url: `http://127.0.0.1:${server.address().port}`,
    bytes,
    close() {
      for (const s of sockets) s.destroy()
      server.close()
    },
  }
}

module.exports = { start }
