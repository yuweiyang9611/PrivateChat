import { codecFromAnnexB, encode, Features, parseAgentFrame, Transport, TransportState, type MonitorInfo, type QualityPreset } from './protocol'
import { RemoteAudio } from './RemoteAudio'
import { RemoteRealtime, realtimeSupported } from './RemoteRealtime'

/** 浏览器能不能解 H.264：要 WebCodecs（Chrome / Edge 94+、Safari 16.4+、Firefox 130+） */
export function webCodecsSupported(): boolean {
  return typeof VideoDecoder !== 'undefined' && typeof EncodedVideoChunk !== 'undefined'
}

export type LinkState = 'connecting' | 'waiting-agent' | 'streaming' | 'reconnecting' | 'closed'

/** target：被控端按网络状况定的码率上限；max：当前画质档位的上限（老版本被控端两个都是 0） */
export interface RemoteStats { fps: number; kbps: number; cpu: number; rtt: number; decoded: number; target: number; max: number }

/** 声音的状态：off 没开；playing 在放；blocked 浏览器要先点一下页面才让出声；unsupported 这个浏览器放不了 */
export type AudioState = 'off' | 'playing' | 'blocked' | 'unsupported'

/**
 * 画面和声音现在走哪条线路：relay 经我们的服务器中继；realtime 经 Cloudflare Realtime（快速线路）；
 * connecting 正在接快速线路（这期间画面仍从中继来）。
 */
export type LineState = 'relay' | 'connecting' | 'realtime'

/** 快速线路接不上时自动再试几次；之后留在中继上，等用户自己再点 */
const REALTIME_AUTO_RETRIES = 2
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
  /** 线路变了。从快速线路退回中继时 reason 是原因（用户自己切回来的没有） */
  line: (s: LineState, reason?: string) => void
}

/**
 * 控制端的数据面：连 /ws/remote/{id}/controller，H.264 关键帧 / 增量帧喂给 WebCodecs，解出来画到 canvas；
 * 每解出一帧回一个 FrameAck、收到探测包回 Pong（Agent 据此估网络能传多快，网络慢时自动降码率，延迟不累积）。
 * 声音（Opus）交给 {@link RemoteAudio} 播放。
 * 断线自动重连（会话是否结束由页面根据 SignalR 判断后调用 close）。
 * 快速线路：被控端支持时（VideoConfig 的功能位），可以让画面和声音改经 Cloudflare Realtime 过来（{@link RemoteRealtime}），
 * 画到同一块 canvas 上；键鼠、剪贴板这些仍走这条 WebSocket。接不上、中途断了都会自己回到中继。
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
  private decodedThisSecond = 0
  private decodedLastSecond = 0
  private readonly ctx: CanvasRenderingContext2D | null
  private quality: QualityPreset | null = null
  private monitor: number | null = null
  private wantAudio = false
  private audio: RemoteAudio | null = null
  private features = 0
  private wantRealtime = false
  private realtime: RemoteRealtime | null = null
  private realtimeGeneration = -1
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
  }

  close(): void {
    this.closed = true
    this.peer = false
    window.clearTimeout(this.retryTimer)
    window.clearInterval(this.statsTimer)
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

  setQuality(q: QualityPreset): void {
    this.quality = q
    this.send(encode.setQuality(q))
  }

  selectMonitor(index: number): void {
    this.monitor = index
    this.send(encode.selectMonitor(index))
  }

  /** 要不要听对方电脑的声音。关掉时被控端就不采集了（不占流量） */
  setAudio(on: boolean): void {
    this.wantAudio = on
    if (this.realtime?.active) {
      // 画面在快速线路上：声音也从那边来
      this.realtime.setAudio(on)
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
    if (this.realtime?.active) return { scheduled: this.realtime.scheduled, peak: this.realtime.peak }
    return { scheduled: this.audio?.scheduled ?? 0, peak: this.audio?.peak ?? 0 }
  }

  // ---- 快速线路 ----

  /** 被控端和这个浏览器都能走快速线路 */
  get realtimeAvailable(): boolean {
    return (this.features & Features.Realtime) !== 0 && realtimeSupported()
  }

  /**
   * 画面和声音想走哪条线路。选快速线路时：被控端支持就去接，接上之前画面照旧从中继来；
   * 接不上或中途断了自己回到中继（line 事件里带原因），过后再自动试几次。
   */
  setTransport(realtime: boolean): void {
    this.wantRealtime = realtime
    this.realtimeFailures = 0
    if (realtime) this.requestRealtime()
    else {
      const was = this.line
      this.dropRealtime()
      if (was !== 'relay') this.send(encode.setTransport(Transport.Relay))
      this.setLine('relay')
    }
  }

  private requestRealtime(): void {
    if (!this.wantRealtime || this.closed || !this.peer || !this.realtimeAvailable) return
    if (this.line !== 'relay' || this.realtimeFailures > REALTIME_AUTO_RETRIES) return
    this.setLine('connecting')
    this.send(encode.setTransport(Transport.Realtime))
    window.clearTimeout(this.realtimeTimer)
    this.realtimeTimer = window.setTimeout(() => this.failRealtime('对方电脑没有回应'), REALTIME_REQUEST_TIMEOUT_MS)
  }

  /** 被控端说它发布好了（或者重新发布了）：去拉流 */
  private pullRealtime(generation: number): void {
    if (!this.wantRealtime || this.closed) {
      this.send(encode.setTransport(Transport.Relay))
      return
    }
    if (this.realtime && this.realtimeGeneration === generation) return
    this.dropRealtime()
    this.realtimeGeneration = generation
    this.setLine('connecting')
    const realtime = new RemoteRealtime(this.sessionId, (video) => this.paint(video), {
      first: () => {
        if (this.realtime !== realtime) return
        window.clearTimeout(this.realtimeTimer)
        // 从这一帧起画面和声音都从快速线路来：中继那边的解码器和声音停掉
        this.audio?.close()
        this.audio = null
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
    })
    this.realtime = realtime
    void realtime.start()
  }

  private sendReport(realtime: RemoteRealtime): void {
    void realtime.report().then((report) => {
      if (report && this.realtime === realtime) this.send(encode.receiverReport(report))
    })
  }

  /** 快速线路不行了：回到中继，告诉被控端，过一会儿再试（次数有限） */
  private failRealtime(reason: string, tellAgent = true): void {
    if (this.line === 'relay' && !this.realtime) return
    this.dropRealtime()
    if (tellAgent) this.send(encode.setTransport(Transport.Relay))
    this.realtimeFailures++
    this.setLine('relay', reason)
    if (this.wantRealtime && this.realtimeFailures <= REALTIME_AUTO_RETRIES) {
      this.realtimeTimer = window.setTimeout(() => this.requestRealtime(), 15_000 * this.realtimeFailures)
    }
  }

  /** 收掉快速线路这边的东西；画面本来在那上面的话，中继这边从关键帧重新开始、声音也接回来 */
  private dropRealtime(): void {
    window.clearTimeout(this.realtimeTimer)
    window.clearInterval(this.reportTimer)
    const realtime = this.realtime
    this.realtime = null
    this.realtimeGeneration = -1
    if (!realtime) return
    const wasActive = realtime.active
    realtime.close()
    if (!wasActive || this.closed) return
    this.needKey = true
    if (this.wantAudio) this.setAudio(true)
    else this.on.audio?.('off')
  }

  private setLine(line: LineState, reason?: string): void {
    if (this.line === line && !reason) return
    this.line = line
    this.on.line?.(line, reason)
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
      // 对面换了一条连接（或者走了）：它那边的快速线路已经收掉，这边也回到中继
      this.dropRealtime()
      this.setLine('relay')
      if (this.peer) {
        // Agent（重新）连上：它会从关键帧开始；把这边选过的画质、显示器、要不要声音再告诉它一遍
        this.needKey = true
        if (this.quality !== null) this.send(encode.setQuality(this.quality))
        if (this.monitor !== null) this.send(encode.selectMonitor(this.monitor))
        this.send(encode.setAudio(this.wantAudio))
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
        this.requestRealtime()
        return
      case 'transport':
        if (f.state === TransportState.Realtime) this.pullRealtime(f.generation)
        else if (f.state === TransportState.RealtimeFailed) this.failRealtime(f.detail || '对方电脑接不上快速线路', false)
        else if (this.line !== 'relay') {
          this.dropRealtime()
          this.setLine('relay')
        }
        return
      case 'video':
        this.onVideo(f.timestamp, f.key, f.data)
        return
      case 'monitors':
        this.on.monitors?.(f.monitors)
        return
      case 'stats':
        this.on.stats?.({ fps: f.fps, kbps: f.kbps, cpu: f.cpu, rtt: f.rtt, decoded: this.decodedLastSecond, target: f.target, max: f.max })
        return
      case 'ping':
        // 原样回去：被控端量的是这一来一回的时间，所以收到就回，不等别的
        this.send(encode.pong(f.sequence))
        return
      case 'audio':
        // 画面已经在快速线路上：声音也从那边来，中继上晚到的这几包不放
        if (!this.realtime?.active) this.audio?.push(f.codec, f.channels, f.timestamp, f.data)
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
    if (this.realtime?.active) {
      // 画面已经在快速线路上：中继上晚到的帧不解了，但照样确认（被控端的码率估计还在等它）
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
      this.send(encode.frameAck(Math.round(frame.timestamp / 1000)))
      // 解到一半时画面切到了快速线路：这一帧不画（不然会闪回旧画面）
      if (this.realtime?.active) return
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
