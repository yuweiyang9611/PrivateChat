import { AudioCodec } from './protocol'

/** 浏览器能不能放远程电脑的声音：要 WebCodecs 的 AudioDecoder（Chrome / Edge 94+、Firefox 130+、Safari 26+） */
export function audioSupported(): boolean {
  return typeof AudioDecoder !== 'undefined' && typeof EncodedAudioChunk !== 'undefined'
    && (typeof AudioContext !== 'undefined' || typeof (window as { webkitAudioContext?: unknown }).webkitAudioContext !== 'undefined')
}

/** 先攒这么久再开始放：网络抖一下不至于马上断音 */
const JITTER_SECONDS = 0.12
/** 排队等着放的超过这么久（网络卡了一下之后一口气到一堆）就丢掉新来的，追上实时——声音不能越放越滞后 */
const MAX_AHEAD_SECONDS = 0.6
const PACKET_MICROSECONDS = 20_000

/**
 * 远程电脑的声音：Opus 包 → AudioDecoder → 一块一块排到 AudioContext 的时间线上，首尾相接。
 * 浏览器规定页面要有过用户操作才能出声：AudioContext 停着时（刷新页面后直接进来）{@link blocked} 为 true，
 * 页面在下一次点击 / 按键时调 {@link resume}。
 */
export class RemoteAudio {
  private readonly ctx: AudioContext
  private readonly gain: GainNode
  private decoder: AudioDecoder | null = null
  private channels = 0
  private nextTime = 0
  private closed = false
  /** 已经排上去播放的采样点数（测试、排查用） */
  scheduled = 0
  /** 最近一块的峰值（0..1；测试用：确认解出来的不是静音） */
  peak = 0
  /** AudioContext 开始 / 停止出声时通知页面 */
  onState: ((blocked: boolean) => void) | null = null
  /** 这个浏览器解不了 */
  onUnsupported: (() => void) | null = null

  constructor() {
    const Ctor = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
    let ctx: AudioContext
    try {
      // 和声音包同一个采样率：不用重采样
      ctx = new Ctor({ sampleRate: 48000, latencyHint: 'interactive' })
    } catch {
      ctx = new Ctor()
    }
    this.ctx = ctx
    this.gain = ctx.createGain()
    this.gain.connect(ctx.destination)
    ctx.onstatechange = () => {
      if (!this.closed) this.onState?.(this.blocked)
    }
  }

  get blocked(): boolean {
    return this.ctx.state !== 'running'
  }

  /** 在用户点击 / 按键的处理函数里调用 */
  resume(): void {
    if (!this.closed && this.ctx.state === 'suspended') void this.ctx.resume().catch(() => undefined)
  }

  push(codec: number, channels: number, timestamp: number, data: Uint8Array): void {
    if (this.closed || codec !== AudioCodec.Opus || channels < 1 || channels > 2) return
    if (!this.decoder || this.decoder.state !== 'configured' || this.channels !== channels) {
      if (!this.configure(channels)) return
    }
    try {
      // Opus 每一包都能独立解，所以都是 key
      this.decoder!.decode(new EncodedAudioChunk({ type: 'key', timestamp: timestamp * 1000, duration: PACKET_MICROSECONDS, data }))
    } catch {
      this.dropDecoder()
    }
  }

  private configure(channels: number): boolean {
    this.dropDecoder()
    try {
      const decoder = new AudioDecoder({
        output: (audio) => this.play(audio),
        error: (e) => {
          if (this.decoder !== decoder) return
          this.decoder = null
          if (e.name === 'NotSupportedError') this.onUnsupported?.()
        },
      })
      decoder.configure({ codec: 'opus', sampleRate: 48000, numberOfChannels: channels })
      this.decoder = decoder
      this.channels = channels
      return true
    } catch {
      this.onUnsupported?.()
      return false
    }
  }

  private dropDecoder(): void {
    if (this.decoder && this.decoder.state !== 'closed') this.decoder.close()
    this.decoder = null
  }

  private play(audio: AudioData): void {
    try {
      if (this.closed) return
      const frames = audio.numberOfFrames
      const buffer = this.ctx.createBuffer(audio.numberOfChannels, frames, audio.sampleRate)
      let peak = 0
      for (let c = 0; c < audio.numberOfChannels; c++) {
        const channel = buffer.getChannelData(c)
        copyChannel(audio, c, channel)
        for (let i = 0; i < frames; i += 16) peak = Math.max(peak, Math.abs(channel[i]!))
      }
      this.peak = peak
      const now = this.ctx.currentTime
      if (this.nextTime < now + 0.02) this.nextTime = now + JITTER_SECONDS // 刚开始，或者断过：重新攒一点缓冲
      else if (this.nextTime > now + MAX_AHEAD_SECONDS) return // 攒太多了：丢掉这一块追上实时
      const source = this.ctx.createBufferSource()
      source.buffer = buffer
      source.connect(this.gain)
      source.start(this.nextTime)
      this.nextTime += buffer.duration
      this.scheduled += frames
    } finally {
      audio.close()
    }
  }

  close(): void {
    this.closed = true
    this.dropDecoder()
    void this.ctx.close().catch(() => undefined)
  }
}

/** 取出一个声道（浮点）。解码器给的可能是交错的，也可能是 16 位整数：先让浏览器转，不肯转就自己拆 */
function copyChannel(audio: AudioData, channel: number, into: Float32Array): void {
  try {
    audio.copyTo(into, { planeIndex: channel, format: 'f32-planar' })
    return
  } catch {
    // 这个浏览器的 copyTo 不做格式转换
  }
  const format = audio.format ?? ''
  const planar = format.endsWith('-planar')
  const count = audio.numberOfFrames * (planar ? 1 : audio.numberOfChannels)
  const stride = planar ? 1 : audio.numberOfChannels
  const offset = planar ? 0 : channel
  const options = { planeIndex: planar ? channel : 0 }
  if (format.startsWith('f32')) {
    const raw = new Float32Array(count)
    audio.copyTo(raw, options)
    for (let i = 0; i < into.length; i++) into[i] = raw[i * stride + offset]!
  } else if (format.startsWith('s16')) {
    const raw = new Int16Array(count)
    audio.copyTo(raw, options)
    for (let i = 0; i < into.length; i++) into[i] = raw[i * stride + offset]! / 32768
  } else if (format.startsWith('s32')) {
    const raw = new Int32Array(count)
    audio.copyTo(raw, options)
    for (let i = 0; i < into.length; i++) into[i] = raw[i * stride + offset]! / 2147483648
  }
}
