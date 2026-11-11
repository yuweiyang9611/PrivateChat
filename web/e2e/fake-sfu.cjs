// 假的 Cloudflare Realtime SFU + TURN 接口，给通话端到端测试用（本机没有 Cloudflare 凭据，CI 也不该打真实服务）。
// 接口形状按官方 OpenAPI（developers.cloudflare.com/realtime/static/realtime-api-2024-05-21.yaml）：
//   POST /v1/apps/{appId}/sessions/new
//   POST /v1/apps/{appId}/sessions/{id}/tracks/new        local（带 offer）/ remote（返回 offer + requiresImmediateRenegotiation）
//   PUT  /v1/apps/{appId}/sessions/{id}/renegotiate
//   PUT  /v1/apps/{appId}/sessions/{id}/tracks/close      force / 协商式
//   GET  /v1/apps/{appId}/sessions/{id}
//   POST /v1/turn/keys/{keyId}/credentials/generate-ice-servers
// 媒体是真的：每个会话在一个 headless Chromium 页面里对应一条 RTCPeerConnection，
// 发布者的轨道收下来再加到订阅者的连接上转发出去。和真实服务一样，上一个 offer 没回 answer 就来新的变更会被 406 拒绝。
// 另有测试专用入口：POST /__test/drop-session/{id} 关掉服务端这条连接，模拟媒体链路中断。
const http = require('http')
const { chromium } = require('playwright')

/** 在 SFU 页面里运行的代码 */
function installSfu() {
  const sessions = new Map()
  // Chrome 只在远端音频轨道接到某个播放元素时才会把它的数据往下送：收下来的音频要先挂到一个 <audio> 上，转发才有声音
  const sinks = []
  const sink = (track) => {
    if (track.kind !== 'audio') return
    const el = new Audio()
    el.srcObject = new MediaStream([track])
    el.play().catch(() => {})
    sinks.push(el)
  }
  const gathered = (pc) =>
    pc.iceGatheringState === 'complete'
      ? Promise.resolve()
      : new Promise((resolve) => {
          const timer = setTimeout(resolve, 2000)
          pc.addEventListener('icegatheringstatechange', () => {
            if (pc.iceGatheringState === 'complete') { clearTimeout(timer); resolve() }
          })
        })
  const fail = (status, errorCode, errorDescription) => ({ __status: status, errorCode, errorDescription })
  const get = (id) => sessions.get(id)

  window.sfu = {
    newSession(id) {
      sessions.set(id, {
        pc: new RTCPeerConnection({ bundlePolicy: 'max-bundle' }),
        /** trackName → { mid, track, closed }：这条会话发布的轨道 */
        published: new Map(),
        /** mid → { location, sessionId, trackName, closed } */
        mids: new Map(),
        awaitingAnswer: false,
      })
      return { sessionId: id }
    },

    async tracksNew(id, body) {
      const s = get(id)
      if (!s) return fail(410, 'session_error', 'session not found')
      if (s.awaitingAnswer) return fail(406, 'invalid_session_description', 'previous offer has not been answered')
      const tracks = body.tracks || []
      const locations = [...new Set(tracks.map((t) => t.location))]
      if (!tracks.length || locations.length !== 1) return fail(400, 'invalid_params', 'tracks must be all local or all remote')

      if (locations[0] === 'local') {
        if (!body.sessionDescription || body.sessionDescription.type !== 'offer') return fail(406, 'invalid_session_description', 'offer required')
        await s.pc.setRemoteDescription(body.sessionDescription)
        const results = tracks.map((t) => {
          const tx = s.pc.getTransceivers().find((x) => x.mid === t.mid)
          if (!tx || !t.trackName) return { trackName: t.trackName, mid: t.mid, errorCode: 'invalid_params', errorDescription: 'unknown mid' }
          s.published.set(t.trackName, { mid: t.mid, track: tx.receiver.track, closed: false })
          sink(tx.receiver.track)
          s.mids.set(t.mid, { location: 'local', trackName: t.trackName, closed: false })
          return { trackName: t.trackName, mid: t.mid }
        })
        await s.pc.setLocalDescription(await s.pc.createAnswer())
        await gathered(s.pc)
        return { requiresImmediateRenegotiation: false, tracks: results, sessionDescription: { type: 'answer', sdp: s.pc.localDescription.sdp } }
      }

      const added = []
      const results = tracks.map((t) => {
        const source = get(t.sessionId)?.published.get(t.trackName)
        if (!source || source.closed) return { sessionId: t.sessionId, trackName: t.trackName, errorCode: 'not_found_track_error', errorDescription: 'track not found' }
        const tx = s.pc.addTransceiver(source.track, { direction: 'sendonly' })
        const result = { sessionId: t.sessionId, trackName: t.trackName }
        added.push({ tx, result })
        return result
      })
      if (!added.length) return { requiresImmediateRenegotiation: false, tracks: results }
      await s.pc.setLocalDescription(await s.pc.createOffer())
      await gathered(s.pc)
      for (const a of added) {
        a.result.mid = a.tx.mid
        s.mids.set(a.tx.mid, { location: 'remote', sessionId: a.result.sessionId, trackName: a.result.trackName, closed: false, tx: a.tx })
      }
      s.awaitingAnswer = true
      return { requiresImmediateRenegotiation: true, tracks: results, sessionDescription: { type: 'offer', sdp: s.pc.localDescription.sdp } }
    },

    async renegotiate(id, body) {
      const s = get(id)
      if (!s) return fail(410, 'session_error', 'session not found')
      if (!s.awaitingAnswer || !body.sessionDescription || body.sessionDescription.type !== 'answer') return fail(406, 'invalid_session_description', 'no offer is waiting for an answer')
      await s.pc.setRemoteDescription(body.sessionDescription)
      s.awaitingAnswer = false
      return {}
    },

    async tracksClose(id, body) {
      const s = get(id)
      if (!s) return fail(410, 'session_error', 'session not found')
      if (!body.force && (!body.sessionDescription || body.sessionDescription.type !== 'offer')) return fail(406, 'invalid_session_description', 'offer required unless force')
      const results = (body.tracks || []).map((t) => {
        const entry = s.mids.get(t.mid)
        if (!entry || entry.closed) return { mid: t.mid, errorCode: 'close_track_error', errorDescription: "Track doesn't exist or was already closed" }
        entry.closed = true
        if (entry.location === 'local') {
          const source = s.published.get(entry.trackName)
          if (source) source.closed = true
          // 源没了：正在转发它的订阅也停
          for (const other of sessions.values())
            for (const m of other.mids.values())
              if (m.location === 'remote' && m.sessionId === id && m.trackName === entry.trackName && m.tx) m.tx.sender.replaceTrack(null).catch(() => {})
        } else if (entry.tx) {
          entry.tx.sender.replaceTrack(null).catch(() => {})
        }
        return { mid: t.mid }
      })
      const response = { requiresImmediateRenegotiation: false, tracks: results }
      if (!body.force) {
        await s.pc.setRemoteDescription(body.sessionDescription)
        await s.pc.setLocalDescription(await s.pc.createAnswer())
        await gathered(s.pc)
        response.sessionDescription = { type: 'answer', sdp: s.pc.localDescription.sdp }
      }
      return response
    },

    state(id) {
      const s = get(id)
      if (!s) return fail(410, 'session_error', 'session not found')
      return {
        tracks: [...s.mids.entries()].map(([mid, m]) => ({ location: m.location, mid, trackName: m.trackName, ...(m.sessionId ? { sessionId: m.sessionId } : {}), status: m.closed ? 'inactive' : 'active' })),
        dataChannels: [],
      }
    },

    /** 测试专用：服务端这条连接没了（链路中断） */
    drop(id) {
      const s = get(id)
      if (!s) return false
      s.pc.close()
      return true
    },

    summary() {
      return [...sessions.entries()].map(([id, s]) => ({ id, state: s.pc.connectionState, published: [...s.published.keys()], pulled: [...s.mids.values()].filter((m) => m.location === 'remote' && !m.closed).length }))
    },
  }
}

/**
 * 启动假 SFU。返回 { url, requests, errors, drop(sessionId), summary(), close() }。
 * requests：每个请求 { method, path, status }；errors：返回了 4xx / 5xx 的请求（测试结束时应为空）。
 */
async function startFakeSfu({ port = 8788, appId = 'e2e-app', appSecret = 'e2e-secret', turnKeyId = 'e2e-turn', turnToken = 'e2e-turn-token' } = {}) {
  const browser = await chromium.launch({
    args: ['--disable-features=WebRtcHideLocalIpsWithMdns', '--allow-loopback-in-peer-connection', '--autoplay-policy=no-user-gesture-required'],
  })
  const page = await (await browser.newContext()).newPage()
  page.on('pageerror', (e) => console.log('  [fake-sfu pageerror]', String(e).slice(0, 300)))
  await page.goto('about:blank')
  await page.evaluate(installSfu)

  const requests = []
  const errors = []
  let seq = 0
  /** 页面里的操作串行执行，避免两个请求同时改同一条连接（真实服务按会话限流，这里从简） */
  let chain = Promise.resolve()
  const run = (op, ...args) => {
    const next = chain.then(() => page.evaluate(([o, a]) => window.sfu[o](...a), [op, args]))
    chain = next.catch(() => undefined)
    return next
  }

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost')
    const chunks = []
    for await (const c of req) chunks.push(c)
    const text = Buffer.concat(chunks).toString('utf8')
    const send = (status, body) => {
      requests.push({ method: req.method, path: url.pathname, status })
      if (status >= 400) errors.push({ method: req.method, path: url.pathname, status, body })
      res.writeHead(status, { 'Content-Type': 'application/json' })
      res.end(JSON.stringify(body))
    }
    try {
      const body = text ? JSON.parse(text) : {}
      const auth = req.headers.authorization || ''
      const path = url.pathname

      if (path === `/v1/turn/keys/${turnKeyId}/credentials/generate-ice-servers` && req.method === 'POST') {
        if (auth !== `Bearer ${turnToken}`) return send(401, { errorCode: 'unauthorized', errorDescription: 'bad turn token' })
        // 本机回环测试不需要 STUN / TURN：给一个空列表，浏览器只用 host 候选
        return send(201, { iceServers: [] })
      }
      const drop = path.match(/^\/__test\/drop-session\/([^/]+)$/)
      if (drop && req.method === 'POST') return send(200, { dropped: await run('drop', drop[1]) })

      const m = path.match(new RegExp(`^/v1/apps/${appId}/sessions/([^/]+)(?:/(.+))?$`))
      if (!m) return send(404, { errorCode: 'not_found', errorDescription: 'unknown app or path' })
      if (auth !== `Bearer ${appSecret}`) return send(401, { errorCode: 'unauthorized', errorDescription: 'bad app secret' })
      const [, id, op] = m

      let result
      if (id === 'new' && !op && req.method === 'POST') {
        result = await run('newSession', `e2e${String(++seq).padStart(4, '0')}${Math.random().toString(16).slice(2, 10)}`)
        return send(201, result)
      } else if (op === 'tracks/new' && req.method === 'POST') result = await run('tracksNew', id, body)
      else if (op === 'renegotiate' && req.method === 'PUT') result = await run('renegotiate', id, body)
      else if (op === 'tracks/close' && req.method === 'PUT') result = await run('tracksClose', id, body)
      else if (!op && req.method === 'GET') result = await run('state', id)
      else return send(404, { errorCode: 'not_found', errorDescription: 'unknown operation' })

      if (result && result.__status) {
        const { __status, ...error } = result
        return send(__status, error)
      }
      return send(200, result)
    } catch (e) {
      return send(500, { errorCode: 'internal_error', errorDescription: String(e).slice(0, 300) })
    }
  })
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(port, '0.0.0.0', resolve)
  })

  return {
    url: `http://localhost:${port}/v1`,
    requests,
    errors,
    drop: (sessionId) => run('drop', sessionId),
    summary: () => run('summary'),
    async close() {
      await new Promise((resolve) => server.close(resolve))
      await browser.close()
    },
  }
}

module.exports = { startFakeSfu }

// 直接运行：node e2e/fake-sfu.cjs [端口]，手动联调时把 api 的 CloudflareRealtime__BaseUrl 指过来
if (require.main === module) {
  startFakeSfu({ port: Number(process.argv[2]) || 8788 }).then((sfu) => console.log(`fake SFU listening on ${sfu.url}（appId e2e-app / secret e2e-secret / turn e2e-turn / e2e-turn-token）`))
}
