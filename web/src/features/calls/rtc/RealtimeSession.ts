import { SerialQueue } from './queue'

export type TrackKind = 'Audio' | 'Video' | 'Screen'
export interface SessionDescription { type: string; sdp: string }
/** 一条远端轨道在应用里的名字：谁发布的、叫什么。Cloudflare 的会话 Id 只有服务端知道 */
export interface RemoteTrackRef { participantId: string; trackName: string; kind: TrackKind }
export interface PulledTrack { participantId: string; trackName: string; kind: TrackKind; mid: string | null; errorCode: string | null }
export interface IceServerConfig { urls: string[]; username?: string | null; credential?: string | null }

/** 信令：每个方法对应一个 /api/calls/{id}/… 接口，由 api 转发给 Cloudflare Realtime */
export interface Signaling {
  publish(offer: SessionDescription, tracks: { mid: string; kind: TrackKind }[]): Promise<{ answer: SessionDescription; tracks: { mid: string; trackName: string; kind: TrackKind }[] }>
  unpublish(trackNames: string[], offer?: SessionDescription): Promise<{ answer: SessionDescription | null }>
  pull(tracks: { participantId: string; trackName: string }[]): Promise<{ requiresImmediateRenegotiation: boolean; offer: SessionDescription | null; tracks: PulledTrack[] }>
  renegotiate(answer: SessionDescription): Promise<void>
  closePulled(mids: string[]): Promise<void>
}

/**
 * 信令请求被服务器明确拒绝时抛出；status 为 0 表示网络不通（结果未知）。
 * detail 是服务器给的原因代码，例如 410 的 replaced：这个人在别的设备上还在通话里。
 */
export class SignalingError extends Error {
  readonly status: number
  readonly detail: string | undefined

  constructor(status: number, message: string, detail?: string) {
    super(message)
    this.name = 'SignalingError'
    this.status = status
    this.detail = detail
  }
}

export type MediaState = 'connecting' | 'connected' | 'unstable'

export interface SessionHandlers {
  onRemoteTrack(ref: RemoteTrackRef, track: MediaStreamTrack): void
  onRemoteTrackGone(ref: RemoteTrackRef): void
  /** 连接断了，或协商走到一半结果不明：这对连接不能再用，上层要重新加入（换新会话）再发布、订阅 */
  onConnectionLost(reason: string): void
  onState?(state: MediaState): void
}

const keyOf = (r: { participantId: string; trackName: string }): string => `${r.participantId}/${r.trackName}`
/** 订阅时源还没出媒体（对方刚发布）会失败：隔一会儿重试，最多这么多次 */
/** 拉不到的轨道（对方还没开始发媒体）先快速重试这么多次，之后放慢但不放弃：对方的摄像头可能过一会儿才出画面 */
const MAX_PULL_RETRIES = 8
const PULL_RETRY_MS = 1500
const SLOW_PULL_RETRY_MS = 10_000
/** ICE 断开后等这么久还没恢复就整体重连 */
const DISCONNECT_GRACE_MS = 5000
/** 这些状态码说明服务器在转发给 Cloudflare 之前就拒绝了：本地回滚即可，连接还能用 */
const REJECTED_BEFORE_FORWARD = new Set([400, 403, 409])

function encodingsFor(kind: TrackKind): RTCRtpEncodingParameters[] | undefined {
  if (kind === 'Video') return [{ maxBitrate: 1_200_000 }]
  if (kind === 'Screen') return [{ maxBitrate: 2_500_000 }]
  return undefined
}

function describe(d: RTCSessionDescription | null): SessionDescription {
  if (!d?.sdp) throw new Error('浏览器没有生成 SDP')
  return { type: d.type, sdp: d.sdp }
}

/** 等 ICE 候选收集完再把 SDP 发出去（Cloudflare 的接口没有 trickle）；最多等 timeoutMs，不让慢的 STUN / TURN 拖住接通 */
function gathered(pc: RTCPeerConnection, timeoutMs = 1000): Promise<void> {
  if (pc.iceGatheringState === 'complete') return Promise.resolve()
  return new Promise((resolve) => {
    const done = (): void => {
      clearTimeout(timer)
      pc.removeEventListener('icegatheringstatechange', check)
      resolve()
    }
    const check = (): void => { if (pc.iceGatheringState === 'complete') done() }
    const timer = setTimeout(done, timeoutMs)
    pc.addEventListener('icegatheringstatechange', check)
  })
}

/**
 * 一个参与者与 Cloudflare Realtime SFU 之间的媒体连接：
 * 两条 RTCPeerConnection，一条只发布自己的轨道（浏览器出 offer），一条只订阅别人的轨道（Cloudflare 出 offer）。
 * 分开之后两个方向的协商互不打架，各自一条串行队列（Cloudflare 官方 video-room 示例的做法）。
 * 本类不碰摄像头 / 麦克风的采集与释放，只负责把给它的轨道发出去、把订阅到的轨道交出来。
 */
export class RealtimeSession {
  private readonly pub: RTCPeerConnection
  private readonly sub: RTCPeerConnection
  private readonly pubQueue = new SerialQueue()
  private readonly subQueue = new SerialQueue()
  private readonly published = new Map<TrackKind, { transceiver: RTCRtpTransceiver; trackName: string }>()
  private wanted = new Map<string, RemoteTrackRef>()
  private readonly pulled = new Map<string, { ref: RemoteTrackRef; mid: string }>()
  private readonly byMid = new Map<string, RemoteTrackRef>()
  private readonly retries = new Map<string, number>()
  private readonly disconnectTimers = new Map<RTCPeerConnection, ReturnType<typeof setTimeout>>()
  /** 连通过的连接：之后掉回 new / closed 说明对端把传输关了 */
  private readonly everConnected = new Set<RTCPeerConnection>()
  private retryTimer: ReturnType<typeof setTimeout> | undefined
  private syncQueued = false
  private closed = false
  private lost = false
  private state: MediaState = 'connecting'
  private readonly signaling: Signaling
  private readonly handlers: SessionHandlers

  constructor(iceServers: IceServerConfig[], signaling: Signaling, handlers: SessionHandlers) {
    this.signaling = signaling
    this.handlers = handlers
    const config: RTCConfiguration = {
      bundlePolicy: 'max-bundle',
      iceServers: iceServers.map((s) => ({
        urls: s.urls,
        ...(s.username ? { username: s.username } : {}),
        ...(s.credential ? { credential: s.credential } : {}),
      })),
    }
    this.pub = new RTCPeerConnection(config)
    this.sub = new RTCPeerConnection(config)

    this.sub.addEventListener('track', (event) => {
      if (this.closed) return
      const mid = event.transceiver.mid
      const ref = mid ? this.byMid.get(mid) : undefined
      if (ref) this.handlers.onRemoteTrack(ref, event.track)
    })
    for (const pc of [this.pub, this.sub]) pc.addEventListener('connectionstatechange', () => this.onConnectionState(pc))
  }

  // ---------------------------------------------------------------- 发布

  /** 发布本地轨道（已经在发布的种类跳过）。返回后对方即可订阅 */
  publish(items: { kind: TrackKind; track: MediaStreamTrack }[]): Promise<void> {
    return this.pubQueue.run(async () => {
      if (this.closed || this.lost) return
      const added = items
        .filter((i) => !this.published.has(i.kind))
        .map((i) => ({ kind: i.kind, transceiver: this.pub.addTransceiver(i.track, { direction: 'sendonly', sendEncodings: encodingsFor(i.kind) }) }))
      if (!added.length) return
      try {
        await this.pub.setLocalDescription(await this.pub.createOffer())
        await gathered(this.pub)
        const tracks = added.map((a) => {
          if (a.transceiver.mid === null) throw new Error('浏览器没有分配 mid')
          return { mid: a.transceiver.mid, kind: a.kind }
        })
        const res = await this.signaling.publish(describe(this.pub.localDescription), tracks)
        await this.pub.setRemoteDescription(res.answer as RTCSessionDescriptionInit)
        const failed: TrackKind[] = []
        for (const a of added) {
          const hit = res.tracks.find((t) => t.mid === a.transceiver.mid)
          if (hit) this.published.set(a.kind, { transceiver: a.transceiver, trackName: hit.trackName })
          else failed.push(a.kind)
        }
        if (failed.length) throw new SignalingError(502, `发布失败：${failed.join(', ')}`)
      } catch (e) {
        if (this.closed) return
        await this.recoverPublisher(e, added.map((a) => a.transceiver))
        throw e
      }
    })
  }

/**
   * 换源不重新协商：摄像头开关、前后摄像头切换。没在发布这种轨道时返回 false。
   * 排在发布队列里：刚进通话、首次发布还没完成时点了关摄像头，要等发布完成再换，
   * 否则换源落空，发出去的是已经停掉的摄像头轨道，对方一直看不到画面。
   */
  replaceTrack(kind: TrackKind, track: MediaStreamTrack | null): Promise<boolean> {
    return this.pubQueue.run(async () => {
      const entry = this.published.get(kind)
      if (!entry || this.closed || this.lost) return false
      await entry.transceiver.sender.replaceTrack(track)
      return true
    })
  }

  isPublished(kind: TrackKind): boolean {
    return this.published.has(kind)
  }

  /** 取消发布（停止共享屏幕）：协商式关闭，订阅方收到 TrackUnpublished 后各自停止接收 */
  unpublish(kind: TrackKind): Promise<void> {
    return this.pubQueue.run(async () => {
      const entry = this.published.get(kind)
      if (!entry || this.closed || this.lost) return
      this.published.delete(kind)
      try {
        entry.transceiver.stop()
        await this.pub.setLocalDescription(await this.pub.createOffer())
        await gathered(this.pub)
        const res = await this.signaling.unpublish([entry.trackName], describe(this.pub.localDescription))
        if (res.answer) await this.pub.setRemoteDescription(res.answer as RTCSessionDescriptionInit)
        else await this.pub.setLocalDescription({ type: 'rollback' })
      } catch (e) {
        if (this.closed) return
        await this.recoverPublisher(e, [])
        throw e
      }
    })
  }

  /**
   * 发布方向协商失败后的处理：服务器在转发前就拒绝的（参数不对、重复发布），回滚本地 offer，连接照常可用；
   * 其余情况（Cloudflare 报错、超时、断网）请求可能已经生效也可能没有，这条连接的 SDP 状态说不清了，整体重连。
   */
  private async recoverPublisher(error: unknown, added: RTCRtpTransceiver[]): Promise<void> {
    if (error instanceof SignalingError && REJECTED_BEFORE_FORWARD.has(error.status) && this.pub.signalingState === 'have-local-offer') {
      try {
        await this.pub.setLocalDescription({ type: 'rollback' })
        for (const t of added) { try { t.stop() } catch { /* 已经停了 */ } }
        return
      } catch {
        // 回滚不了就走重连
      }
    }
    this.fail('publish')
  }

  // ---------------------------------------------------------------- 订阅

  /** 声明"现在想收哪些轨道"：新增的去订阅，不要了的关闭。可以随时反复调用，内部串行对账 */
  setSubscriptions(refs: RemoteTrackRef[]): void {
    this.wanted = new Map(refs.map((r) => [keyOf(r), r]))
    for (const k of [...this.retries.keys()]) if (!this.wanted.has(k)) this.retries.delete(k)
    this.scheduleSync()
  }

  private scheduleSync(): void {
    if (this.closed || this.lost || this.syncQueued) return
    this.syncQueued = true
    this.subQueue
      .run(() => {
        this.syncQueued = false
        return this.sync()
      })
      .catch(() => undefined)
  }

  private async sync(): Promise<void> {
    if (this.closed || this.lost) return

    const removed = [...this.pulled.values()].filter((p) => !this.wanted.has(keyOf(p.ref)))
    if (removed.length) {
      for (const p of removed) {
        this.pulled.delete(keyOf(p.ref))
        this.byMid.delete(p.mid)
        this.handlers.onRemoteTrackGone(p.ref)
      }
      // 让 Cloudflare 停止转发；失败无妨（对方已经不发了）
      await this.signaling.closePulled(removed.map((p) => p.mid)).catch(() => undefined)
    }

    const missing = [...this.wanted.values()].filter((r) => !this.pulled.has(keyOf(r)))
    if (!missing.length || this.closed) return

    let retry = false
    try {
      const res = await this.signaling.pull(missing.map((r) => ({ participantId: r.participantId, trackName: r.trackName })))
      if (this.closed) return
      for (const t of res.tracks) {
        const k = keyOf(t)
        const ref = this.wanted.get(k) ?? missing.find((m) => keyOf(m) === k)
        if (!ref) continue
        if (t.mid) {
          // 先登记 mid → 轨道，再应用 offer：track 事件在 setRemoteDescription 里就会触发
          this.pulled.set(k, { ref, mid: t.mid })
          this.byMid.set(t.mid, ref)
          this.retries.delete(k)
        } else {
          this.retries.set(k, (this.retries.get(k) ?? 0) + 1)
          retry = true
        }
      }
      if (res.requiresImmediateRenegotiation && res.offer) {
        await this.sub.setRemoteDescription(res.offer as RTCSessionDescriptionInit)
        await this.sub.setLocalDescription(await this.sub.createAnswer())
        await gathered(this.sub)
        // Cloudflare 收下 answer 才算这次订阅完成，之后才能做下一个变更
        await this.signaling.renegotiate(describe(this.sub.localDescription))
      }
    } catch (e) {
      if (!this.closed) this.fail('subscribe')
      throw e
    }
    if (retry && !this.closed) {
      const attempts = Math.min(...missing.filter((r) => !this.pulled.has(keyOf(r))).map((r) => this.retries.get(keyOf(r)) ?? 0))
      clearTimeout(this.retryTimer)
      this.retryTimer = setTimeout(() => this.scheduleSync(), attempts < MAX_PULL_RETRIES ? PULL_RETRY_MS : SLOW_PULL_RETRY_MS)
    }
  }

  // ---------------------------------------------------------------- 连接状态

  private onConnectionState(pc: RTCPeerConnection): void {
    if (this.closed) return
    const timer = this.disconnectTimers.get(pc)
    if (pc.connectionState === 'connected') this.everConnected.add(pc)
    if (pc.connectionState === 'failed') {
      this.fail('ice-failed')
    } else if (this.everConnected.has(pc) && (pc.connectionState === 'closed' || pc.connectionState === 'new')) {
      // 对端关闭了传输（Cloudflare 回收了会话）：按规范聚合状态会回到 new / closed，而不是 failed
      this.fail('transport-closed')
    } else if (pc.connectionState === 'disconnected') {
      // 换网络、信号抖动时会短暂断开，多半自己能恢复；过了宽限期再整体重连
      if (timer === undefined) this.disconnectTimers.set(pc, setTimeout(() => this.fail('ice-disconnected'), DISCONNECT_GRACE_MS))
    } else if (timer !== undefined) {
      clearTimeout(timer)
      this.disconnectTimers.delete(pc)
    }
    this.reportState()
  }

  private reportState(): void {
    // 还没协商过的那条连接停在 new，不算
    const states = [this.pub, this.sub].map((pc) => pc.connectionState).filter((s) => s !== 'new')
    const next: MediaState = states.some((s) => s === 'disconnected' || s === 'failed')
      ? 'unstable'
      : states.length > 0 && states.every((s) => s === 'connected') ? 'connected' : 'connecting'
    if (next !== this.state) {
      this.state = next
      this.handlers.onState?.(next)
    }
  }

  private fail(reason: string): void {
    if (this.lost || this.closed) return
    this.lost = true
    this.handlers.onConnectionLost(reason)
  }

  /** 关闭两条连接。本地采集的轨道不在这里停（重连时还要接着用） */
  close(): void {
    if (this.closed) return
    this.closed = true
    this.pubQueue.close()
    this.subQueue.close()
    clearTimeout(this.retryTimer)
    for (const t of this.disconnectTimers.values()) clearTimeout(t)
    this.disconnectTimers.clear()
    this.pub.close()
    this.sub.close()
  }
}
