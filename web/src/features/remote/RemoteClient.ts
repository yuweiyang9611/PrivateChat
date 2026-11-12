import { codecFromAnnexB, encode, parseAgentFrame, type MonitorInfo, type QualityPreset } from './protocol'

/** 浏览器能不能解 H.264：要 WebCodecs（Chrome / Edge 94+、Safari 16.4+、Firefox 130+） */
export function webCodecsSupported(): boolean {
  return typeof VideoDecoder !== 'undefined' && typeof EncodedVideoChunk !== 'undefined'
}

export type LinkState = 'connecting' | 'waiting-agent' | 'streaming' | 'reconnecting' | 'closed'

export interface RemoteStats { fps: number; kbps: number; cpu: number; rtt: number; decoded: number }

export interface RemoteClientEvents {
  state: (s: LinkState) => void
  config: (c: { width: number; height: number; fps: number; monitor: number }) => void
  monitors: (m: MonitorInfo[]) => void
  stats: (s: RemoteStats) => void
  status: (code: number, message: string) => void
  clipboard: (text: string) => void
  /** 服务器以 4001 / 4000 关闭：会话结束 / 在别处打开了 */
  ended: (why: 'ended' | 'replaced') => void
  unsupported: () => void
}

/**
 * 控制端的数据面：连 /ws/remote/{id}/controller，H.264 关键帧 / 增量帧喂给 WebCodecs，解出来画到 canvas；
 * 每解出一帧回一个 FrameAck（Agent 据此流控，网络慢时自动降帧，延迟不累积）。
 * 断线自动重连（会话是否结束由页面根据 SignalR 判断后调用 close）。
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
        this.closed = true
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
    window.clearTimeout(this.retryTimer)
    window.clearInterval(this.statsTimer)
    const ws = this.ws
    this.ws = null
    if (ws && ws.readyState <= WebSocket.OPEN) ws.close(1000)
    if (this.decoder && this.decoder.state !== 'closed') this.decoder.close()
    this.decoder = null
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

  private onNotice(text: string): void {
    try {
      const notice = JSON.parse(text) as { type?: string; connected?: boolean }
      if (notice.type !== 'peer') return
      this.peer = !!notice.connected
      this.on.state?.(this.peer ? 'streaming' : 'waiting-agent')
      if (this.peer) {
        // Agent（重新）连上：它会从关键帧开始；把这边选过的画质、显示器再告诉它一遍
        this.needKey = true
        if (this.quality !== null) this.send(encode.setQuality(this.quality))
        if (this.monitor !== null) this.send(encode.selectMonitor(this.monitor))
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
        this.width = f.width
        this.height = f.height
        this.canvas.width = f.width
        this.canvas.height = f.height
        this.needKey = true
        this.on.config?.(f)
        return
      case 'video':
        this.onVideo(f.timestamp, f.key, f.data)
        return
      case 'monitors':
        this.on.monitors?.(f.monitors)
        return
      case 'stats':
        this.on.stats?.({ fps: f.fps, kbps: f.kbps, cpu: f.cpu, rtt: f.rtt, decoded: this.decodedLastSecond })
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
      if (this.canvas.width !== frame.displayWidth || this.canvas.height !== frame.displayHeight) {
        this.canvas.width = frame.displayWidth
        this.canvas.height = frame.displayHeight
      }
      this.ctx?.drawImage(frame, 0, 0, this.canvas.width, this.canvas.height)
      this.decodedThisSecond++
      this.send(encode.frameAck(Math.round(frame.timestamp / 1000)))
    } finally {
      frame.close()
    }
  }
}
