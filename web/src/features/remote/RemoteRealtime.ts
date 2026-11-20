import { api } from '@/api/client'
import { unwrap } from '@/stores/remote'
import { gathered } from '@/features/calls/rtc/RealtimeSession'
import type { ReceiverReport } from './protocol'

/** 浏览器有没有 WebRTC（快速线路要用） */
export function realtimeSupported(): boolean {
  return typeof RTCPeerConnection !== 'undefined'
}

export interface RemoteRealtimeEvents {
  /** 第一帧画面已经出来：可以从中继切过来了 */
  first: () => void
  /** 接不上，或者接上以后断了、不出画面了 */
  failed: (reason: string) => void
  /** 声音：playing 在放；blocked 浏览器要先点一下页面才让出声；off 没开 */
  audio: (state: 'off' | 'playing' | 'blocked') => void
  /** 画面卡在一个怎么要都补不回来的包上：该让对方发一个关键帧了（见 StuckWatch） */
  stuck: () => void
}

/** 接上之后这么久还没出第一帧：算接不上 */
const FIRST_FRAME_TIMEOUT_MS = 10_000
/** 出过画面之后这么久没有新的一帧：算断了。屏幕不动时被控端也会每 2 秒发一帧保活 */
const STALL_TIMEOUT_MS = 7_000
/** 连接断开这么久没恢复：算断了 */
const DISCONNECT_TIMEOUT_MS = 5_000
/** 两次检查隔了这么久：定时器停过（页面被冻结、电脑睡过），这段时间不算"没有画面" */
const TIMER_GAP_MS = 3_000

/**
 * 判断"出过画面之后很久没有新的一帧"。
 * 页面在后台时（切到别的标签页、窗口最小化或被盖住）浏览器不再回调"出了一帧"，画面其实还在收；
 * 定时器停过一阵（页面被冻结、电脑睡过）也一样。这些时间都不算，不然一切走标签页快速线路就被当成断了。
 * 线路真断了的话，后台期间由连接状态和被控端那边发现。
 */
export class StallWatch {
  private lastFrameAt = 0
  private lastCheckAt = Number.NEGATIVE_INFINITY

  /** 出了一帧 */
  frame(now: number): void {
    this.lastFrameAt = now
  }

  /** 每秒查一次。visible：页面现在在前台 */
  stalled(now: number, visible: boolean): boolean {
    if (!visible || now - this.lastCheckAt > TIMER_GAP_MS) this.lastFrameAt = now
    this.lastCheckAt = now
    return now - this.lastFrameAt > STALL_TIMEOUT_MS
  }
}

/**
 * 出过画面之后这么久一个包都没收到（画面和声音都没有）：线路断了。
 * 被控端在这条线路上每 2 秒至少发一帧画面和一个声音包（屏幕不动、没声音时也发），连着两轮都没到才会超过这个时间。
 */
const SILENT_TIMEOUT_MS = 4_500

/**
 * 判断"线路上什么都收不到了"。
 * 真实的 Cloudflare 上偶尔会这样：连接还显示连着，但 SFU 不再转发任何东西（原因没查到）。
 * 等"7 秒没有新画面"才发现太慢——屏幕不动时本来就可以几秒没有新画面，所以那个时间不能再短；
 * 而一个包都收不到是另一回事，保活的包两秒就该来一轮。
 * 页面在后台、定时器停过的时间不算（和 StallWatch 一样）。
 */
export class SilenceWatch {
  private packets = -1
  /** 见过包数在涨：浏览器要是根本不报这个数（一直是 0），就不能拿它判断 */
  private counting = false
  private lastPacketAt = 0
  private lastCheckAt = Number.NEGATIVE_INFINITY

  /** 每取到一次接收统计调一次；packets 是画面和声音一共收到的包数。返回 true：很久一个包都没到 */
  sample(now: number, packets: number): boolean {
    if (this.packets >= 0 && packets > this.packets) this.counting = true
    if (packets !== this.packets || now - this.lastCheckAt > TIMER_GAP_MS) this.lastPacketAt = now
    this.packets = packets
    this.lastCheckAt = now
    return this.counting && now - this.lastPacketAt > SILENT_TIMEOUT_MS
  }
}

/** 多久看一次接收统计（"是不是卡在补不回来的包上""是不是什么都收不到了"） */
const STUCK_POLL_MS = 200
/** 要了重发，既没补到包、也没解出新的一帧，过了这么久（网络往返慢的话是 3 个往返）：不等了 */
const STUCK_QUIET_MS = 400
/** ……而且这期间至少要过这么多次：只要过一两次的，补的包可能还在路上 */
const STUCK_QUIET_NACKS = 3
/** 丢包多的网络上别的包一直在补到、唯独卡住的那个补不回来：这么久没解出新的一帧（或 5 个往返）也不等了 */
const STUCK_LONG_MS = 1_000
const STUCK_LONG_NACKS = 5
/** 要过一次关键帧之后，至少隔这么久才再要 */
const STUCK_COOLDOWN_MS = 1_500

/** 接收统计里和"解不解得出来"有关的几个累计数 */
export interface DecodeProgress {
  /** 解出来的帧数 */
  framesDecoded: number
  /** 向 SFU 要过几次重发 */
  nackCount: number
  /** 补到的包数（浏览器不报这个数时是 0） */
  retransmitted: number
  /** 到 SFU 的往返时间，还没量到是 0 */
  rttMs: number
}

/**
 * 判断"画面卡在一个补不回来的包上"。
 * H.264 的一帧缺一个包就解不出来，后面的帧也跟着解不了。浏览器会向 SFU 要重发，绝大多数时候 0.1 秒内就补到；
 * 偶尔 SFU 手里没有那个包（真实的 Cloudflare 上几分钟一次），浏览器就一直要、一直要不到，固定等满 3 秒才向发送方要关键帧，画面停 3 秒。
 * 这里盯着接收统计：要了重发却没有进展，过一小会儿就自己去要关键帧，不陪它等。
 * 屏幕不动（没有新的一帧，但也没人要重发）、一大帧还在路上（没要重发，或者要了就补到）都不算卡住。
 */
export class StuckWatch {
  private decoded = -1
  private nacks = 0
  private retransmitted = 0
  /** 从什么时候起"要了重发，没补到包也没解出新的一帧"；null：没在等 */
  private quietSince: number | null = null
  private quietNacks = 0
  /** 从什么时候起"要了重发，没解出新的一帧"（中间补到过别的包也算在里面） */
  private waitingSince: number | null = null
  private waitingNacks = 0
  private askedAt = Number.NEGATIVE_INFINITY

  /** 每取到一次接收统计调一次。返回 true：现在该要关键帧 */
  sample(now: number, s: DecodeProgress): boolean {
    const first = this.decoded < 0
    const decodedMore = s.framesDecoded !== this.decoded
    const recovered = s.retransmitted !== this.retransmitted
    const asked = Math.max(0, s.nackCount - this.nacks)
    this.decoded = s.framesDecoded
    this.nacks = s.nackCount
    this.retransmitted = s.retransmitted
    if (first || decodedMore) {
      this.forget()
      return false
    }
    if (recovered) {
      this.quietSince = null
      this.quietNacks = 0
    } else if (asked > 0) {
      this.quietSince ??= now
      this.quietNacks += asked
    }
    if (asked > 0) {
      this.waitingSince ??= now
      this.waitingNacks += asked
    }
    const quiet =
      this.quietSince !== null && this.quietNacks >= STUCK_QUIET_NACKS && now - this.quietSince >= Math.max(STUCK_QUIET_MS, 3 * s.rttMs)
    const long =
      this.waitingSince !== null && this.waitingNacks >= STUCK_LONG_NACKS && now - this.waitingSince >= Math.max(STUCK_LONG_MS, 5 * s.rttMs)
    if (!(quiet || long) || now - this.askedAt < STUCK_COOLDOWN_MS) return false
    this.askedAt = now
    this.forget()
    return true
  }

  private forget(): void {
    this.quietSince = this.waitingSince = null
    this.quietNacks = this.waitingNacks = 0
  }
}

/** 页面现在在前台（没有这个属性的环境当作在前台） */
export function pageVisible(): boolean {
  return typeof document === 'undefined' || document.visibilityState !== 'hidden'
}

interface Sample { at: number; lost: number; received: number; bytes: number; delay: number; emitted: number }

/**
 * 快速线路的接收端：画面和声音经 Cloudflare Realtime（WebRTC）过来，不经我们的服务器中继。
 * 信令经我们的接口：问 ICE 服务器 → 拉流拿到 offer → 交回 answer。
 * 画面用一个不挂到页面上的 video 元素解码，每出一帧交给 paint 画到和中继共用的那块 canvas 上
 * （页面上的布局、坐标换算、全屏都不用管画面是从哪条线路来的）；声音用单独的 audio 元素放。
 */
export class RemoteRealtime {
  private pc: RTCPeerConnection | null = null
  private readonly video = document.createElement('video')
  private readonly sound = new Audio()
  private hasSound = false
  private wantAudio = false
  private closed = false
  private started = false
  private readonly stall = new StallWatch()
  private readonly stuck = new StuckWatch()
  private readonly silence = new SilenceWatch()
  private stuckTimer: number | undefined
  private lastTime = -1
  private framesSinceReport = 0
  private previous: Sample | null = null
  private firstTimer: number | undefined
  private disconnectTimer: number | undefined
  private meter: { context: AudioContext; analyser: AnalyserNode; data: Float32Array<ArrayBuffer> } | null = null
  /** 测试用：最近一次量到的声音峰值（0..1） */
  peak = 0

  private readonly sessionId: string
  private readonly paint: (video: HTMLVideoElement) => void
  private readonly on: RemoteRealtimeEvents

  constructor(sessionId: string, paint: (video: HTMLVideoElement) => void, on: RemoteRealtimeEvents) {
    this.sessionId = sessionId
    this.paint = paint
    this.on = on
    this.video.muted = true
    this.video.playsInline = true
    this.video.autoplay = true
  }

  /** 画面已经在这条线路上出来了 */
  get active(): boolean {
    return this.started && !this.closed
  }

  async start(): Promise<void> {
    const path = { id: this.sessionId }
    try {
      const info = await unwrap(api.GET('/api/remote/sessions/{id}/realtime', { params: { path } }))
      if (this.closed) return
      if (!info.available) throw new Error('服务器没有开快速线路')
      const pc = new RTCPeerConnection({
        bundlePolicy: 'max-bundle',
        iceServers: info.iceServers.map((s) => ({ urls: s.urls, username: s.username ?? undefined, credential: s.credential ?? undefined })),
      })
      this.pc = pc
      pc.ontrack = (e) => this.onTrack(e)
      pc.onconnectionstatechange = () => this.onConnectionState(pc)

      const pulled = await unwrap(api.POST('/api/remote/sessions/{id}/realtime/pull', { params: { path } }))
      if (this.closed) return
      await pc.setRemoteDescription({ type: 'offer', sdp: pulled.offer })
      const answer = await pc.createAnswer()
      try {
        // 声明能收双声道：不写的话 Chrome 会把对方电脑的声音混成单声道
        await pc.setLocalDescription({ type: 'answer', sdp: withStereo(answer.sdp ?? '') })
      } catch {
        await pc.setLocalDescription(answer)
      }
      await gathered(pc)
      if (this.closed) return
      await unwrap(api.POST('/api/remote/sessions/{id}/realtime/renegotiate', { params: { path }, body: { answer: pc.localDescription?.sdp ?? '' } }))
      if (this.closed) return
      this.firstTimer = window.setTimeout(() => {
        if (!this.started) this.fail('10 秒没有出画面')
      }, FIRST_FRAME_TIMEOUT_MS)
    } catch (e) {
      this.fail(e instanceof Error ? e.message : String(e))
    }
  }

  private onTrack(e: RTCTrackEvent): void {
    // 远程桌面要的是跟手：不要为了平滑多缓冲
    try {
      e.receiver.jitterBufferTarget = 0
    } catch {
      // 老浏览器没有这个属性
    }
    if (e.track.kind === 'video') {
      this.video.srcObject = new MediaStream([e.track])
      void this.video.play().catch(() => undefined)
      this.watch()
    } else {
      this.sound.srcObject = new MediaStream([e.track])
      this.hasSound = true
      this.applyAudio()
    }
  }

  /** 每出一帧画一次。没有 requestVideoFrameCallback 的浏览器按屏幕刷新去看播放位置变了没有 */
  private watch(): void {
    const video = this.video
    // 类型定义里它总是有的，老一些的浏览器（Firefox 132 以前）其实没有
    const perFrame = typeof (video as { requestVideoFrameCallback?: unknown }).requestVideoFrameCallback === 'function'
    const next = (): void => {
      if (this.closed) return
      if (perFrame) video.requestVideoFrameCallback(onFrame)
      else requestAnimationFrame(onFrame)
    }
    const onFrame = (): void => {
      if (this.closed) return
      const fresh = perFrame || video.currentTime !== this.lastTime
      if (fresh && video.videoWidth > 0) {
        this.lastTime = video.currentTime
        this.stall.frame(performance.now())
        this.framesSinceReport++
        this.paint(video)
        if (!this.started) {
          this.started = true
          window.clearTimeout(this.firstTimer)
          this.stuckTimer = window.setInterval(() => void this.checkReceiving(), STUCK_POLL_MS)
          this.applyAudio()
          this.on.first()
        }
      }
      next()
    }
    next()
  }

  private onConnectionState(pc: RTCPeerConnection): void {
    if (this.closed || this.pc !== pc) return
    window.clearTimeout(this.disconnectTimer)
    if (pc.connectionState === 'failed' || pc.connectionState === 'closed') this.fail('连接断开')
    else if (pc.connectionState === 'disconnected') {
      // 网络抖一下可能自己恢复
      this.disconnectTimer = window.setTimeout(() => {
        if (this.pc === pc && pc.connectionState !== 'connected') this.fail('连接断开')
      }, DISCONNECT_TIMEOUT_MS)
    }
  }

  /**
   * 出了画面之后隔一小会儿看一眼接收统计：
   * 什么都收不到了就判定线路断了（由调用方重新接）；卡在补不回来的包上就报 stuck（由调用方去要关键帧）。
   * 页面在后台时不看：那时浏览器可能不解画面，会被当成卡住。
   */
  private async checkReceiving(): Promise<void> {
    const pc = this.pc
    if (!pc || this.closed || !pageVisible()) return
    let framesDecoded = -1
    let nackCount = 0
    let retransmitted = 0
    let rttMs = 0
    let packets = 0
    try {
      const stats = await pc.getStats()
      stats.forEach((s: Record<string, unknown>) => {
        if (s.type === 'inbound-rtp') packets += Number(s.packetsReceived ?? 0)
        if (s.type === 'inbound-rtp' && s.kind === 'video') {
          framesDecoded = Number(s.framesDecoded ?? 0)
          nackCount = Number(s.nackCount ?? 0)
          retransmitted = Number(s.retransmittedPacketsReceived ?? 0)
        } else if (s.type === 'candidate-pair' && s.nominated && s.currentRoundTripTime !== undefined) {
          rttMs = Number(s.currentRoundTripTime) * 1000
        }
      })
    } catch {
      return
    }
    if (this.closed || this.pc !== pc || framesDecoded < 0) return
    const now = performance.now()
    if (this.silence.sample(now, packets)) {
      this.fail('收不到对方的数据')
      return
    }
    if (this.stuck.sample(now, { framesDecoded, nackCount, retransmitted, rttMs })) this.on.stuck()
  }

  /**
   * 每秒调一次：这一秒收得怎么样（发给被控端，它据此调码率）。还没出画面时返回 null。
   * 出过画面之后太久没有新的一帧，这里会判定线路断了（页面在后台的时间不算，见 StallWatch）。
   */
  async report(): Promise<ReceiverReport | null> {
    const pc = this.pc
    if (!pc || !this.active) return null
    const now = performance.now()
    if (this.stall.stalled(now, pageVisible())) {
      this.fail('很久没有收到画面')
      return null
    }
    let lost = 0
    let received = 0
    let bytes = 0
    let delay = 0
    let emitted = 0
    let rttMs = 0
    try {
      const stats = await pc.getStats()
      stats.forEach((s: Record<string, unknown>) => {
        if (s.type === 'inbound-rtp' && s.kind === 'video') {
          lost = Number(s.packetsLost ?? 0)
          received = Number(s.packetsReceived ?? 0)
          bytes = Number(s.bytesReceived ?? 0)
          delay = Number(s.jitterBufferDelay ?? 0)
          emitted = Number(s.jitterBufferEmittedCount ?? 0)
        } else if (s.type === 'candidate-pair' && s.nominated && s.currentRoundTripTime !== undefined) {
          rttMs = Number(s.currentRoundTripTime) * 1000
        }
      })
    } catch {
      return null
    }
    this.measure()
    const before = this.previous
    this.previous = { at: now, lost, received, bytes, delay, emitted }
    const frames = this.framesSinceReport
    this.framesSinceReport = 0
    // 帧率至少报 1：画面在这条线路上（屏幕不动时一秒里可能一帧都没有），被控端靠它知道这边接上了
    if (!before) return { lossPermille: 0, receivedKbps: 0, jitterBufferMs: 0, fps: Math.max(1, frames), rttMs }
    const seconds = Math.max(0.2, (now - before.at) / 1000)
    const lostNow = Math.max(0, lost - before.lost)
    const receivedNow = Math.max(0, received - before.received)
    const emittedNow = emitted - before.emitted
    return {
      lossPermille: lostNow + receivedNow > 0 ? (1000 * lostNow) / (lostNow + receivedNow) : 0,
      receivedKbps: (Math.max(0, bytes - before.bytes) * 8) / 1000 / seconds,
      jitterBufferMs: emittedNow > 0 ? (1000 * (delay - before.delay)) / emittedNow : 0,
      fps: Math.max(1, frames / seconds),
      rttMs,
    }
  }

  // ---- 声音 ----

  setAudio(on: boolean): void {
    this.wantAudio = on
    this.applyAudio()
  }

  /** 在点击 / 按键的处理函数里调：浏览器要有过用户操作才让页面出声 */
  resumeAudio(): void {
    if (this.wantAudio && this.sound.paused) this.applyAudio()
    void this.meter?.context.resume().catch(() => undefined)
  }

  /** 出画面之前不放声音：这时候中继那边的声音还在放，两边一起放会听到两遍 */
  private applyAudio(): void {
    if (this.closed || !this.started) return
    if (!this.wantAudio || !this.hasSound) {
      this.sound.pause()
      this.on.audio('off')
      return
    }
    this.sound.play().then(
      () => {
        if (this.closed || !this.wantAudio) return
        this.on.audio('playing')
        this.startMeter()
      },
      () => {
        if (!this.closed && this.wantAudio) this.on.audio('blocked')
      },
    )
  }

  /** 测试用：已经放出来的声音采样点数（按 48 kHz 算） */
  get scheduled(): number {
    return Math.round(this.sound.currentTime * 48000)
  }

  /** 量一下正在放的声音有多响（只量不放：放是 audio 元素的事）。量不了不影响听 */
  private startMeter(): void {
    if (this.meter || !(this.sound.srcObject instanceof MediaStream)) return
    try {
      const context = new AudioContext()
      const analyser = context.createAnalyser()
      analyser.fftSize = 2048
      context.createMediaStreamSource(this.sound.srcObject).connect(analyser)
      this.meter = { context, analyser, data: new Float32Array(analyser.fftSize) }
    } catch {
      this.meter = null
    }
  }

  private measure(): void {
    const meter = this.meter
    if (!meter || this.sound.paused) {
      this.peak = 0
      return
    }
    meter.analyser.getFloatTimeDomainData(meter.data)
    let peak = 0
    for (const v of meter.data) peak = Math.max(peak, Math.abs(v))
    this.peak = peak
  }

  private fail(reason: string): void {
    if (this.closed) return
    this.close()
    this.on.failed(reason)
  }

  close(): void {
    if (this.closed) return
    this.closed = true
    window.clearTimeout(this.firstTimer)
    window.clearTimeout(this.disconnectTimer)
    window.clearInterval(this.stuckTimer)
    const pc = this.pc
    this.pc = null
    if (pc) {
      pc.ontrack = null
      pc.onconnectionstatechange = null
      pc.close()
    }
    this.video.pause()
    this.video.srcObject = null
    this.sound.pause()
    this.sound.srcObject = null
    void this.meter?.context.close().catch(() => undefined)
    this.meter = null
  }
}

/** 在 answer 的 Opus 参数里加上 stereo=1（已经有的不动） */
export function withStereo(sdp: string): string {
  return sdp.replace(/^(a=fmtp:\d+ (?!.*stereo=)[^\r\n]*useinbandfec=1[^\r\n]*)$/gm, '$1;stereo=1')
}
