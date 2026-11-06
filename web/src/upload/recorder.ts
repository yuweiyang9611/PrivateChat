import { ref, type Ref } from 'vue'

/** 按浏览器支持度挑录音格式：Chrome / Firefox 给 webm/opus，Safari 给 mp4(AAC)；服务端统一转 m4a */
const CANDIDATES = ['audio/webm;codecs=opus', 'audio/mp4', 'audio/ogg;codecs=opus', 'audio/webm']

export function pickMimeType(): string {
  if (typeof MediaRecorder === 'undefined') return ''
  return CANDIDATES.find((m) => MediaRecorder.isTypeSupported(m)) ?? ''
}

export function extensionFor(mime: string): string {
  if (mime.startsWith('audio/mp4')) return 'm4a'
  if (mime.startsWith('audio/ogg')) return 'ogg'
  return 'webm'
}

export interface Recording { file: File; duration: number }

export type RecorderUnavailableReason = 'insecure' | 'unsupported' | 'denied'

export class RecorderUnavailableError extends Error {
  readonly reason: RecorderUnavailableReason
  constructor(reason: RecorderUnavailableReason) {
    super(reason)
    this.reason = reason
  }
}

/**
 * 语音录制：MediaRecorder + AnalyserNode 取音量用于波形动画。
 * 一个实例只录一次。
 */
export class VoiceRecorder {
  readonly seconds: Ref<number> = ref(0)
  /** 0–1 当前音量 */
  readonly level: Ref<number> = ref(0)

  private stream: MediaStream | null = null
  private recorder: MediaRecorder | null = null
  private audioCtx: AudioContext | null = null
  private chunks: Blob[] = []
  private startedAt = 0
  private timer = 0
  private raf = 0
  private mime = ''

  private readonly maxSeconds: number
  private readonly onMax?: () => void

  constructor(maxSeconds = 300, onMax?: () => void) {
    this.maxSeconds = maxSeconds
    this.onMax = onMax
  }

  async start(): Promise<void> {
    if (!window.isSecureContext) throw new RecorderUnavailableError('insecure')
    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') throw new RecorderUnavailableError('unsupported')
    this.mime = pickMimeType()
    if (!this.mime) throw new RecorderUnavailableError('unsupported')
    try {
      this.stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } })
    } catch {
      throw new RecorderUnavailableError('denied')
    }

    this.recorder = new MediaRecorder(this.stream, { mimeType: this.mime, audioBitsPerSecond: 48_000 })
    this.recorder.ondataavailable = (e) => { if (e.data.size) this.chunks.push(e.data) }
    this.recorder.start(250)
    this.startedAt = Date.now()
    this.timer = window.setInterval(() => {
      this.seconds.value = Math.floor((Date.now() - this.startedAt) / 1000)
      if (this.seconds.value >= this.maxSeconds) this.onMax?.()
    }, 200)

    try {
      this.audioCtx = new AudioContext()
      const src = this.audioCtx.createMediaStreamSource(this.stream)
      const analyser = this.audioCtx.createAnalyser()
      analyser.fftSize = 256
      src.connect(analyser)
      const buf = new Uint8Array(analyser.frequencyBinCount)
      const tick = (): void => {
        analyser.getByteTimeDomainData(buf)
        let sum = 0
        for (const v of buf) { const d = (v - 128) / 128; sum += d * d }
        this.level.value = Math.min(1, Math.sqrt(sum / buf.length) * 3)
        this.raf = requestAnimationFrame(tick)
      }
      tick()
    } catch {
      // 没有 AudioContext 也能录，只是没波形
    }
  }

  /** 结束并返回文件；时长不足 1 秒视为误触，返回 null */
  async stop(): Promise<Recording | null> {
    const recorder = this.recorder
    if (!recorder) return null
    const duration = (Date.now() - this.startedAt) / 1000
    const blob = await new Promise<Blob>((resolve) => {
      recorder.onstop = () => resolve(new Blob(this.chunks, { type: this.mime.split(';')[0] }))
      if (recorder.state !== 'inactive') recorder.stop()
      else resolve(new Blob(this.chunks, { type: this.mime.split(';')[0] }))
    })
    this.release()
    if (duration < 1 || blob.size === 0) return null
    const stamp = new Date().toISOString().replace(/[:.]/g, '-')
    const file = new File([blob], `voice-${stamp}.${extensionFor(this.mime)}`, { type: blob.type })
    return { file, duration: Math.round(duration * 10) / 10 }
  }

  cancel(): void {
    if (this.recorder && this.recorder.state !== 'inactive') this.recorder.stop()
    this.release()
    this.chunks = []
  }

  private release(): void {
    window.clearInterval(this.timer)
    cancelAnimationFrame(this.raf)
    this.stream?.getTracks().forEach((t) => t.stop())
    void this.audioCtx?.close()
    this.stream = null
    this.recorder = null
    this.audioCtx = null
    this.level.value = 0
  }
}
