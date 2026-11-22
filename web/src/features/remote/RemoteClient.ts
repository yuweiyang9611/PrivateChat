import { codecFromAnnexB, encode, Features, legacyQualityOf, parseAgentFrame, Transport, TransportState, type MonitorInfo, type QualityPreset, type VideoSettings } from './protocol'
import { RemoteAudio } from './RemoteAudio'
import { RemoteRealtime, pageVisible, realtimeSupported } from './RemoteRealtime'
import { FrameRateGovernor } from './FrameRateGovernor'

/** 浏览器能不能解 H.264：要 WebCodecs（Chrome / Edge 94+、Safari 16.4+、Firefox 130+） */
export function webCodecsSupported(): boolean {
  return typeof VideoDecoder !== 'undefined' && typeof EncodedVideoChunk !== 'undefined'
}

export type LinkState = 'connecting' | 'waiting-agent' | 'streaming' | 'reconnecting' | 'closed'

/**
 * target：被控端按网络状况定的码率上限；max：当前画质档位的上限（老版本被控端两个都是 0）。
 * captureMs / encodeMs：对方电脑上每帧采集、编码平均花多久；flags：StatsFlags（这三个 0.8.6 起才有，老版本是 0）。
 * fpsCap：这边请对方每秒最多发几帧（MaxFps，中继线路上这边解码跟不上时才有），0 = 没限
 */
export interface RemoteStats {
  fps: number; kbps: number; cpu: number; rtt: number; decoded: number; target: number; max: number
  captureMs: number; encodeMs: number; flags: number; fpsCap: number
}

/** 声音的状态：off 没开；playing 在放；blocked 浏览器要先点一下页面才让出声；unsupported 这个浏览器放不了 */
export type AudioState = 'off' | 'playing' | 'blocked' | 'unsupported'

/**
 * 画面和声音现在走哪条线路：relay 经我们的服务器中继（标准线路）；realtime 经 Cloudflare Realtime（快速线路）；
 * connecting 选的是快速线路，但它还没接上（或者出了问题、正在重新接）——这期间没有画面，不拿中继的顶上。
 */
export type LineState = 'relay' | 'connecting' | 'realtime'

/** 快速线路出了什么问题；retrying：还在自己重新接（false = 试过几次停下来了，等用户点重试或者切到标准线路） */
export interface LineProblem { reason: string; retrying: boolean }

/** 快速线路出问题后隔多久重新接一次（接的还是这条线路）。都试完还不行就停下来，等用户决定 */
const REALTIME_RETRY_DELAYS_MS = [2_000, 5_000, 10_000, 15_000, 15_000]
/** 线路本来在出画面、中途断了：第一次重接只等这么一小会儿（让对方先把旧的连接收掉） */
const REALTIME_QUICK_RETRY_MS = 300
/** 让被控端接快速线路，这么久没有回音就算了 */
const REALTIME_REQUEST_TIMEOUT_MS = 30_000

export interface RemoteClientEvents {
  state: (s: LinkState) => void
  config: (c: { width: number; height: number; fps: number; monitor: number; features: number }) => void
  monitors: (m: MonitorInfo[]) => void
  stats: (s: RemoteStats) => void
  status: (code: number, message: string) => void
  clipboard: (text: string) => void
  /** 服务器以 4001 / 4000 关闭：会话结束 / 在别处打开了 */
  ended: (why: 'ended' | 'replaced') => void
  unsupported: () => void
  audio: (s: AudioState) => void
  /** 线路的状态变了。线路只由用户切；快速线路出了问题时带着 problem，页面据此提示用户 */
  line: (s: LineState, problem?: LineProblem) => void
}

/**
 * 控制端的数据面：连 /ws/remote/{id}/controller，H.264 关键帧 / 增量帧喂给 WebCodecs，解出来画到 canvas；
 * 每解出一帧回一个 FrameAck、收到探测包回 Pong（Agent 据此估网络能传多快，网络慢时自动降码率，延迟不累积）。
 * 声音（Opus）交给 {@link RemoteAudio} 播放。
 * 断线自动重连（会话是否结束由页面根据 SignalR 判断后调用 close）。
 * 快速线路：被控端支持时（VideoConfig 的功能位），可以让画面和声音改经 Cloudflare Realtime 过来（{@link RemoteRealtime}），
 * 画到同一块 canvas 上；键鼠、剪贴板这些仍走这条 WebSocket。走哪条线路由用户选，两条线路之间不自动换：
 * 选了快速线路就只用它，接不上、中途断了都只是重新接它并提示用户，不拿中继的画面顶上。
 */
export class RemoteClient {
  private ws: WebSocket | null = null
  private decoder: VideoDecoder | null = null
  private codec = ''
  private needKey = true
  private lastKeyRequest = 0
  private closed = false
  private retry = 0
  private retryTimer: number | undefined
  private statsTimer: number | undefined
  /** 中继线路上这边解码跟不上时，请被控端少发几帧（见 FrameRateGovernor） */
  private readonly governor = new FrameRateGovernor()
  private governorTimer: number | undefined
  private probeTimer: number | undefined
  /** 量主线程排队多久：往自己的消息队列里投一条，看它等了多久才轮到 */
  private readonly probeChannel = new MessageChannel()
  private decodedThisSecond = 0
  private decodedLastSecond = 0
  private readonly ctx: CanvasRenderingContext2D | null
  private quality: QualityPreset | null = null
  /** 选过的画质（SetVideo）；只用老的两档（老被控端）时是 null */
  private video: VideoSettings | null = null
  private monitor: number | null = null
  private wantAudio = false
  private audio: RemoteAudio | null = null
  private features = 0
  private wantRealtime = false
  private realtime: RemoteRealtime | null = null
  private realtimeGeneration = -1
  /** 已经让被控端去接快速线路，还在等它回话 */
  private realtimeAsked = false
  private realtimeFailures = 0
  private realtimeTimer: number | undefined
  private reportTimer: number | undefined
  private line: LineState = 'relay'
  private fps = 0
  private shownMonitor = -1
  width = 0
  height = 0
  peer = false

  private readonly sessionId: string
  private readonly canvas: HTMLCanvasElement
  private readonly on: Partial<RemoteClientEvents>

  constructor(sessionId: string, canvas: HTMLCanvasElement, on: Partial<RemoteClientEvents>) {
    this.sessionId = sessionId
    this.canvas = canvas
    this.on = on
    this.ctx = canvas.getContext('2d', { alpha: false })
    document.addEventListener('visibilitychange', this.onVisibility)
    this.probeChannel.port1.onmessage = (e: MessageEvent<number>) => {
      const now = performance.now()
      this.governor.probe(now, now - e.data, this.decoder?.decodeQueueSize ?? 0)
    }
  }

  /** 页面回到前台：在后台时没去接（重新接）的快速线路，现在接；在后台时的帧率判断不作数，先缓一缓 */
  private readonly onVisibility = (): void => {
    if (!pageVisible()) return
    this.governor.hold(performance.now(), 2000)
    this.requestRealtime()
  }

  connect(): void {
    if (this.closed || this.ws) return
    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    const ws = new WebSocket(`${proto}://${location.host}/ws/remote/${this.sessionId}/controller`)
    ws.binaryType = 'arraybuffer'
    this.ws = ws
    this.on.state?.(this.retry === 0 ? 'connecting' : 'reconnecting')
    ws.onopen = () => {
      this.retry = 0
    }
    ws.onmessage = (e: MessageEvent) => {
      if (typeof e.data === 'string') this.onNotice(e.data)
      else this.onFrame(e.data as ArrayBuffer)
    }
    ws.onclose = (e: CloseEvent) => {
      if (this.ws !== ws) return
      this.ws = null
      this.peer = false
      if (e.code === 4001 || e.code === 4000) {
        this.close()
        this.on.state?.('closed')
        this.on.ended?.(e.code === 4000 ? 'replaced' : 'ended')
        return
      }
      if (this.closed) return
      this.on.state?.('reconnecting')
      const delay = Math.min(5000, 500 * 2 ** this.retry++)
      this.retryTimer = window.setTimeout(() => this.connect(), delay)
    }
    window.clearInterval(this.statsTimer)
    this.statsTimer = window.setInterval(() => {
      this.decodedLastSecond = this.decodedThisSecond
      this.decodedThisSecond = 0
    }, 1000)
    window.clearInterval(this.governorTimer)
    this.governorTimer = window.setInterval(() => this.govern(), 500)
    window.clearInterval(this.probeTimer)
    this.probeTimer = window.setInterval(() => {
      if (this.peer && !this.onFastLine && pageVisible()) this.probeChannel.port2.postMessage(performance.now())
    }, 100)
  }

  /** 中继线路上看看这边跟不跟得上；跟不上就请被控端每秒少发几帧，一直很轻松再放开（页面在后台时不看） */
  private govern(): void {
    if (!this.peer || this.onFastLine || !pageVisible()) return
    const cap = this.governor.tick(performance.now(), this.fps)
    if (cap !== null) this.send(encode.maxFps(cap))
  }

  close(): void {
    this.closed = true
    this.peer = false
    document.removeEventListener('visibilitychange', this.onVisibility)
    window.clearTimeout(this.retryTimer)
    window.clearInterval(this.statsTimer)
    window.clearInterval(this.governorTimer)
    window.clearInterval(this.probeTimer)
    this.probeChannel.port1.close()
    this.probeChannel.port2.close()
    const ws = this.ws
    this.ws = null
    if (ws && ws.readyState <= WebSocket.OPEN) ws.close(1000)
    if (this.decoder && this.decoder.state !== 'closed') this.decoder.close()
    this.decoder = null
    this.dropRealtime()
    this.audio?.close()
    this.audio = null
  }

  /** 发一帧；鼠标移动在发送缓冲积压时丢掉（键盘、点击照发） */
  send(frame: Uint8Array, droppable = false): void {
    const ws = this.ws
    if (!ws || ws.readyState !== WebSocket.OPEN || !this.peer) return
    if (droppable && ws.bufferedAmount > 256 * 1024) return
    ws.send(frame)
  }

  /**
   * 选画质。连上之前调也行：先记着，对方连上来时发（见 onNotice）。
   * 新被控端认 SetVideo、不理后面那条 SetQuality；老被控端（0.8.5 及以前）不认识 SetVideo，按 SetQuality 用最接近的一档
   */
  setVideo(s: VideoSettings): void {
    this.video = { ...s }
    this.quality = legacyQualityOf(s)
    this.sendVideo()
  }

  /** 老被控端的两档画质（工具栏上只给老被控端显示这个）：之后只发 SetQuality */
  setQuality(q: QualityPreset): void {
    this.video = null
    this.quality = q
    this.send(encode.setQuality(q))
  }

  private sendVideo(): void {
    if (this.video) this.send(encode.setVideo(this.video))
    if (this.quality !== null) this.send(encode.setQuality(this.quality))
  }

  selectMonitor(index: number): void {
    this.monitor = index
    this.send(encode.selectMonitor(index))
  }

  /** 要不要听对方电脑的声音。关掉时被控端就不采集了（不占流量） */
  setAudio(on: boolean): void {
    this.wantAudio = on
    if (this.onFastLine) {
      // 选的是快速线路：声音也只从那边来（还没接上时先记着，接上了再放）
      this.realtime?.setAudio(on)
      if (!on) this.on.audio?.('off')
      this.send(encode.setAudio(on))
      return
    }
    if (!on) {
      this.audio?.close()
      this.audio = null
      this.on.audio?.('off')
    } else if (!this.audio && !this.closed) {
      const audio = new RemoteAudio()
      audio.onState = (blocked) => {
        if (this.audio === audio) this.on.audio?.(blocked ? 'blocked' : 'playing')
      }
      audio.onUnsupported = () => {
        if (this.audio !== audio) return
        audio.close()
        this.audio = null
        this.wantAudio = false
        this.send(encode.setAudio(false))
        this.on.audio?.('unsupported')
      }
      this.audio = audio
      this.on.audio?.(audio.blocked ? 'blocked' : 'playing')
    }
    this.send(encode.setAudio(on))
  }

  /** 在点击 / 按键的处理函数里调：浏览器要有过用户操作才让页面出声 */
  resumeAudio(): void {
    this.audio?.resume()
    this.realtime?.resumeAudio()
  }

  /** 测试用：已经排上去播放的声音采样点数、最近一块的峰值 */
  get audioProgress(): { scheduled: number; peak: number } {
    if (this.onFastLine) return { scheduled: this.realtime?.scheduled ?? 0, peak: this.realtime?.peak ?? 0 }
    return { scheduled: this.audio?.scheduled ?? 0, peak: this.audio?.peak ?? 0 }
  }

  // ---- 快速线路 ----

  /** 被控端和这个浏览器都能走快速线路 */
  get realtimeAvailable(): boolean {
    return (this.features & Features.Realtime) !== 0 && realtimeSupported()
  }

  /**
   * 这次远程的画面和声音只从快速线路来：用户选了它，而且被控端和这个浏览器都支持。
   * 线路不自动换——这条线路没接上、出了问题的时候也不解、不画、不放中继上的画面和声音。
   */
  private get onFastLine(): boolean {
    return this.wantRealtime && this.realtimeAvailable
  }

  /**
   * 画面和声音走哪条线路，由用户定。选快速线路时（被控端支持的话）只用它：接上之前没有画面；
   * 接不上、中途断了就重新接这条线路并在 line 事件里带上原因，要不要切到标准线路由用户决定。
   */
  setTransport(realtime: boolean): void {
    this.wantRealtime = realtime
    this.realtimeFailures = 0
    this.applyTransport()
  }

  /** 快速线路试了几次都没接上、停下来之后，用户点了"重试" */
  retryRealtime(): void {
    if (!this.onFastLine || this.realtime || this.realtimeAsked) return
    window.clearTimeout(this.realtimeTimer)
    this.realtimeFailures = 0
    this.on.line?.('connecting')
    this.requestRealtime()
  }

  /** 按用户选的线路（和被控端支不支持）把这边摆对。选择变了、被控端报上来支不支持的时候调 */
  private applyTransport(): void {
    if (this.onFastLine) {
      if (this.line === 'relay') {
        // 从标准线路换过来：中继那边的声音停掉，画面也不再解（见 onVideo）
        this.audio?.close()
        this.audio = null
        this.setLine('connecting')
        // 为中继这边解码请对方少发的帧数，快速线路上不作数（那边不靠这边确认）
        const uncapped = this.governor.reset()
        if (uncapped !== null) this.send(encode.maxFps(uncapped))
      }
      this.requestRealtime()
      return
    }
    if (this.line === 'relay') return
    // 换到标准线路：收掉快速线路，中继这边从关键帧重新开始，声音也接回来
    this.dropRealtime()
    this.send(encode.setTransport(Transport.Relay))
    this.setLine('relay')
    this.needKey = true
    this.governor.hold(performance.now(), 2000)
    if (this.wantAudio) this.setAudio(true)
    else this.on.audio?.('off')
  }

  private requestRealtime(): void {
    if (!this.onFastLine || this.closed || !this.peer) return
    // 已经在接了；或者试过几次停下来了（等用户点重试）
    if (this.realtime || this.realtimeAsked || this.realtimeFailures > REALTIME_RETRY_DELAYS_MS.length) return
    // 页面在后台时不接：浏览器这时不回调"出了一帧"，接上了也等不到第一帧，白白算一次失败。回到前台再接（onVisibility）
    if (!pageVisible()) return
    this.realtimeAsked = true
    this.send(encode.setTransport(Transport.Realtime))
    window.clearTimeout(this.realtimeTimer)
    this.realtimeTimer = window.setTimeout(() => this.failRealtime('对方电脑没有回应'), REALTIME_REQUEST_TIMEOUT_MS)
  }

  /** 被控端说它发布好了（或者重新发布了）：去拉流 */
  private pullRealtime(generation: number): void {
    if (!this.onFastLine || this.closed) {
      this.send(encode.setTransport(Transport.Relay))
      return
    }
    if (this.realtime && this.realtimeGeneration === generation) return
    this.dropRealtime()
    this.realtimeGeneration = generation
    const realtime = new RemoteRealtime(this.sessionId, (video) => this.paint(video), {
      first: () => {
        if (this.realtime !== realtime) return
        window.clearTimeout(this.realtimeTimer)
        this.realtimeFailures = 0
        realtime.setAudio(this.wantAudio)
        this.setLine('realtime')
        this.sendReport(realtime)
        this.reportTimer = window.setInterval(() => this.sendReport(realtime), 1000)
      },
      failed: (reason) => {
        if (this.realtime === realtime) this.failRealtime(reason)
      },
      audio: (state) => {
        if (this.realtime === realtime && realtime.active) this.on.audio?.(state)
      },
      stuck: () => {
        // 有个包怎么要都补不回来：浏览器自己要等满 3 秒才向对方要关键帧，这里经控制通道直接要
        if (this.realtime === realtime) this.requestKey()
      },
    })
    this.realtime = realtime
    void realtime.start()
  }

  private sendReport(realtime: RemoteRealtime): void {
    void realtime.report().then((report) => {
      if (report && this.realtime === realtime) this.send(encode.receiverReport(report))
    })
  }

  /**
   * 快速线路没接上，或者接上以后断了：不换到标准线路。收掉这次的连接，把原因告诉页面，过一会儿重新接这条线路；
   * 连着几次都不行就停下来，等用户点"重试"或者自己切到标准线路。
   */
  private failRealtime(reason: string, tellAgent = true): void {
    // 用户已经切走了；或者这次失败已经算过了（两边各报一次）
    if (!this.onFastLine || (!this.realtime && !this.realtimeAsked)) return
    // 这条线路刚才还在出画面（不是一直没接上）：第一次重接不用等，画面已经停着了
    const wasShowing = this.line === 'realtime'
    this.dropRealtime()
    if (tellAgent) this.send(encode.setTransport(Transport.Relay))
    this.realtimeFailures++
    const delay = wasShowing && this.realtimeFailures === 1 ? REALTIME_QUICK_RETRY_MS : REALTIME_RETRY_DELAYS_MS[this.realtimeFailures - 1]
    this.setLine('connecting', { reason, retrying: delay !== undefined })
    if (delay !== undefined) this.realtimeTimer = window.setTimeout(() => this.requestRealtime(), delay)
  }

  /** 收掉快速线路这边正在接 / 已经接上的连接 */
  private dropRealtime(): void {
    window.clearTimeout(this.realtimeTimer)
    window.clearInterval(this.reportTimer)
    this.realtimeAsked = false
    const realtime = this.realtime
    this.realtime = null
    this.realtimeGeneration = -1
    realtime?.close()
  }

  private setLine(line: LineState, problem?: LineProblem): void {
    if (this.line === line && !problem) return
    this.line = line
    this.on.line?.(line, problem)
  }

  /** 快速线路出了一帧：画到同一块 canvas 上 */
  private paint(video: HTMLVideoElement): void {
    if (this.closed || !video.videoWidth || !video.videoHeight) return
    if (this.canvas.width !== video.videoWidth || this.canvas.height !== video.videoHeight) {
      this.canvas.width = video.videoWidth
      this.canvas.height = video.videoHeight
    }
    this.ctx?.drawImage(video, 0, 0, this.canvas.width, this.canvas.height)
    this.decodedThisSecond++
  }

  private onNotice(text: string): void {
    try {
      const notice = JSON.parse(text) as { type?: string; connected?: boolean }
      if (notice.type !== 'peer') return
      this.peer = !!notice.connected
      this.on.state?.(this.peer ? 'streaming' : 'waiting-agent')
      // 对面换了一条连接（或者走了）：它那边的快速线路已经收掉，这边的也收掉；等它回来再按选的线路接
      this.dropRealtime()
      // 它那边请求过的帧率上限也作废了：从头看
      this.governor.reset()
      this.realtimeFailures = 0
      this.line = this.onFastLine ? 'connecting' : 'relay'
      this.on.line?.(this.line) // 之前提示过的问题也一并清掉
      if (this.peer) {
        // Agent（重新）连上：它会从关键帧开始；把这边选过的画质、显示器、要不要声音再告诉它一遍
        this.needKey = true
        // 画质：SetVideo 后面跟一条 SetQuality（新被控端不理它，老被控端只认它）。收到 VideoConfig 时不要再发：
        // 老被控端每收到一次 SetQuality 都会重发 VideoConfig，那样就来回没完了
        this.sendVideo()
        if (this.monitor !== null) this.send(encode.selectMonitor(this.monitor))
        this.send(encode.setAudio(this.wantAudio))
        // 告诉它这边跟不上时会说（MaxFps，现在不限）：被控端 0.8.4 起收到过这一条才按最高 30 帧发，
        // 没收到（还没更新的旧页面）就照旧按 20 / 15 帧发，免得旧页面在慢设备上跟不上又说不出来
        this.send(encode.maxFps(0))
        // 要不要走快速线路等它的 VideoConfig 来了再说（里面有它支不支持）
      }
    } catch {
      // 不认识的文本消息
    }
  }

  private onFrame(buffer: ArrayBuffer): void {
    const f = parseAgentFrame(buffer)
    if (!f) return
    switch (f.type) {
      case 'config':
        this.features = f.features
        // 被控端问到服务器有快速线路后会把配置原样再发一遍（只为带上那个功能位）：画面没变，不要清画布、不要重新要关键帧
        if (f.width !== this.width || f.height !== this.height || f.fps !== this.fps || f.monitor !== this.shownMonitor) {
          this.width = f.width
          this.height = f.height
          this.fps = f.fps
          this.shownMonitor = f.monitor
          if (!this.realtime?.active) {
            // 画面在快速线路上时 canvas 跟着收到的画面走，不在这里清
            this.canvas.width = f.width
            this.canvas.height = f.height
            this.needKey = true
          }
        }
        this.on.config?.(f)
        this.applyTransport()
        return
      case 'transport':
        if (f.state === TransportState.Realtime) this.pullRealtime(f.generation)
        else if (f.state === TransportState.RealtimeFailed) this.failRealtime(f.detail || '对方电脑接不上快速线路', false)
        // Relay：被控端回应"改回中继"（这边切到了标准线路，或者刚告诉它这条线路断了），不用再做什么
        return
      case 'video':
        this.onVideo(f.timestamp, f.key, f.data)
        return
      case 'monitors':
        this.on.monitors?.(f.monitors)
        return
      case 'stats':
        this.on.stats?.({
          fps: f.fps, kbps: f.kbps, cpu: f.cpu, rtt: f.rtt, decoded: this.decodedLastSecond, target: f.target, max: f.max,
          captureMs: f.captureMs, encodeMs: f.encodeMs, flags: f.flags, fpsCap: this.governor.cap,
        })
        return
      case 'ping':
        // 原样回去：被控端量的是这一来一回的时间，所以收到就回，不等别的
        this.send(encode.pong(f.sequence))
        return
      case 'audio':
        // 选的是快速线路：声音只从那边来，中继上的不放
        if (!this.onFastLine) this.audio?.push(f.codec, f.channels, f.timestamp, f.data)
        return
      case 'status':
        this.on.status?.(f.code, f.message)
        return
      case 'clipboard':
        this.on.clipboard?.(f.text)
        return
    }
  }

  private onVideo(timestamp: number, key: boolean, data: Uint8Array): void {
    if (this.closed) return
    if (this.onFastLine) {
      // 选的是快速线路：中继上的画面不解、不画（线路不自动换），但照样确认（被控端的码率估计还在等它）。
      // 以后用户切到标准线路时，中继这边要从关键帧开始
      this.needKey = true
      this.send(encode.frameAck(timestamp))
      return
    }
    if (this.needKey && !key) {
      // 等关键帧期间丢掉的帧也确认掉：Agent 的在途窗口才会腾出来，不会卡 3 秒
      this.send(encode.frameAck(timestamp))
      this.requestKey()
      return
    }
    if (key) {
      const codec = codecFromAnnexB(data)
      if (codec && (codec !== this.codec || !this.decoder || this.decoder.state === 'closed')) this.configure(codec)
      if (!this.decoder) return
      this.needKey = false
    }
    if (!this.decoder || this.decoder.state !== 'configured') {
      this.needKey = true
      this.requestKey()
      return
    }
    try {
      this.governor.received(timestamp, performance.now(), this.decoder.decodeQueueSize)
      this.decoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp: timestamp * 1000, data }))
    } catch {
      this.dropDecoder()
    }
  }

  private configure(codec: string): void {
    if (this.decoder && this.decoder.state !== 'closed') this.decoder.close()
    this.decoder = null
    try {
      const decoder = new VideoDecoder({
        output: (frame) => this.draw(frame),
        error: (e) => {
          if (this.decoder !== decoder) return
          if (e.name === 'NotSupportedError') {
            // 这个浏览器解不了：断开，不要让 Agent 一直编关键帧
            this.close()
            this.on.unsupported?.()
            return
          }
          // 解码出错（丢了一帧之类）：从下一个关键帧重新开始
          this.dropDecoder()
        },
      })
      // 编码器按 Annex B 输出（SPS/PPS 在关键帧里），不需要 description
      decoder.configure({ codec, optimizeForLatency: true })
      this.decoder = decoder
      // 解码器刚建好，头几帧本来就慢
      this.governor.hold(performance.now(), 2000)
      this.codec = codec
    } catch {
      this.close()
      this.on.unsupported?.()
    }
  }

  private dropDecoder(): void {
    if (this.decoder && this.decoder.state !== 'closed') this.decoder.close()
    this.decoder = null
    this.needKey = true
    this.requestKey()
  }

  private requestKey(): void {
    const now = performance.now()
    if (now - this.lastKeyRequest < 500) return
    this.lastKeyRequest = now
    this.send(encode.requestKeyFrame())
  }

  private draw(frame: VideoFrame): void {
    try {
      const timestamp = Math.round(frame.timestamp / 1000)
      this.governor.decoded(timestamp, performance.now())
      this.send(encode.frameAck(timestamp))
      // 解到一半时用户切到了快速线路：这一帧不画
      if (this.onFastLine) return
      if (this.canvas.width !== frame.displayWidth || this.canvas.height !== frame.displayHeight) {
        this.canvas.width = frame.displayWidth
        this.canvas.height = frame.displayHeight
      }
      this.ctx?.drawImage(frame, 0, 0, this.canvas.width, this.canvas.height)
      this.decodedThisSecond++
    } finally {
      frame.close()
    }
  }
}
