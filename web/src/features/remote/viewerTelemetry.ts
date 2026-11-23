import type { TelemetryStat, ViewerPlaybackTelemetry, ViewerSessionTelemetry } from './telemetry'
import type { FastPlaybackDelta } from './playbackCounters'
import type { ViewerStartupTelemetry } from './telemetry'
import { ViewerStartup } from './viewerStartup'

// 控制端这边的遥测（docs/DESIGN.md"远程协助遥测"）：这次远程里这个浏览器看到的画面怎么样——每秒解出几帧、卡住过几次、
// 要过几次关键帧、请对方少发过几次、快速线路断过几次……RemoteClient 在事情发生的地方各记一笔，每秒给一次状态；
// 远程页面每 60 秒、结束 / 离开 / 关页面时把汇总（ViewerSessionTelemetry）报给服务器。服务器按会话覆盖，所以每份都是从头到现在的汇总。
// 纯计算：不碰网络、不碰 DOM（浏览器和屏幕的信息由 viewerEnvironment 另外取），时间都由调用方传进来（performance.now()）。
// 每秒的数、出错的说明和被控端那边（src/PrivateChat.Agent/Session/SessionTelemetry.cs）的算法一样：
// 百分位取排好序后的第 ceil(p×n) 个，保留一位小数；每个数最多留 2 小时的样本，满了隔一个扔一个；相同的说明只留一条、后面写次数。

/** 画面这么久没出新的一帧，算卡住 */
export const FREEZE_MS = 500
/**
 * ……而且这段时间里至少有这么久是"该到的帧没到"：按帧在对方电脑上的时间算，下一帧比上一帧晚了多少，这边就该晚多少；
 * 这边晚得比那边多出这么多才算。对方屏幕不动的时候本来就没有新帧（中继线路上一帧不发，快速线路上 2 秒一帧保活），那不是卡住
 */
export const LATE_MS = 250
/** 对方最多每秒 30 帧：连着出画面时，两帧在对方电脑上本来就隔这么久 */
const FRAME_MS = 1000 / 30
/**
 * 要过关键帧的那次卡住从哪儿算起：最早从第一次要关键帧之前这么久（快速线路上卡在补不回来的包上，最少等 1 秒才去要，见 RemoteRealtime 的 StuckWatch）。
 * 不从上一帧算：对方屏幕不动时上一帧可能是很久以前的，那段时间本来就没有新帧
 */
const KEY_LOOKBACK_MS = 1000
/** 两次记时之间隔得太久（页面被冻结、电脑睡过）：这一段不算进时长 */
const MAX_GAP_MS = 70_000
/** 每个数最多留这么多个样本（每秒一个，2 小时）：再多就隔一个扔一个 */
export const MAX_KEPT_SAMPLES = 7200
/** 出错、线路问题各最多报这么多条：不同的说明多了，后来的合成最后一条"另有 N 次别的问题" */
export const MAX_NOTES = 20
/** 每条说明最多这么多字（服务器收 300 字，后面还要加"（N 次）"） */
export const MAX_NOTE_LENGTH = 160

const EMPTY_STAT: TelemetryStat = { samples: 0, mean: 0, min: 0, p10: 0, p50: 0, p90: 0, max: 0 }

const round1 = (n: number): number => Math.round(n * 10) / 10
const num = (v: unknown): number => (typeof v === 'number' && Number.isFinite(v) ? v : 0)

/** 一个每秒一个样本的数：全部样本的个数、和、最小、最大，加上留下来算百分位的那些（满了减半） */
export class Series {
  private kept: number[] = []
  /** 每几个样本留一个（减半一次翻一倍） */
  private stride = 1
  private count = 0
  private sum = 0
  private min = Number.POSITIVE_INFINITY
  private max = Number.NEGATIVE_INFINITY

  add(value: number): void {
    if (!Number.isFinite(value)) return
    if (this.count % this.stride === 0) {
      this.kept.push(value)
      if (this.kept.length >= MAX_KEPT_SAMPLES) {
        // 隔一个扔一个：留下第 0、2、4……个，之后也隔一个记一个——留下的始终均匀铺满整次远程
        this.kept = this.kept.filter((_, i) => i % 2 === 0)
        this.stride *= 2
      }
    }
    this.count++
    this.sum += value
    this.min = Math.min(this.min, value)
    this.max = Math.max(this.max, value)
  }

  get samples(): number {
    return this.count
  }

  stat(): TelemetryStat {
    if (!this.count || !this.kept.length) return { ...EMPTY_STAT }
    const sorted = [...this.kept].sort((a, b) => a - b)
    // 第 ceil(p×n) 个（减一点点：0.1×30 这种乘出来可能比 3 大一丝）
    const pick = (p: number): number => round1(sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(p * sorted.length - 1e-9) - 1))]!)
    return { samples: this.count, mean: round1(this.sum / this.count), min: round1(this.min), p10: pick(0.1), p50: pick(0.5), p90: pick(0.9), max: round1(this.max) }
  }

  toJSON(): SavedSeries {
    return { kept: [...this.kept], stride: this.stride, count: this.count, sum: this.sum, min: this.count ? this.min : 0, max: this.count ? this.max : 0 }
  }

  static from(saved: unknown): Series {
    const s = new Series()
    const o = (saved ?? {}) as Partial<SavedSeries>
    const kept = Array.isArray(o.kept) ? o.kept.filter((v): v is number => typeof v === 'number' && Number.isFinite(v)).slice(0, MAX_KEPT_SAMPLES - 1) : []
    s.count = Math.max(kept.length, Math.floor(num(o.count)))
    if (!s.count) return s
    s.kept = kept
    s.stride = Math.max(1, Math.floor(num(o.stride)))
    s.sum = num(o.sum)
    s.min = num(o.min)
    s.max = num(o.max)
    return s
  }
}

interface SavedSeries { kept: number[]; stride: number; count: number; sum: number; min: number; max: number }

/** 出过的问题：同样的说明只留一条、记次数；不同的说明最多 MAX_NOTES - 1 条，再来新的合成最后一条"另有 N 次别的问题" */
export class Notes {
  private items: { text: string; count: number }[] = []
  private others = 0

  add(text: string): void {
    const clipped = clipNote(text)
    if (!clipped) return
    const known = this.items.find((n) => n.text === clipped)
    if (known) known.count++
    else if (this.items.length < MAX_NOTES - 1) this.items.push({ text: clipped, count: 1 })
    else this.others++
  }

  list(): string[] {
    const list = this.items.map((n) => (n.count > 1 ? `${n.text}（${n.count} 次）` : n.text))
    if (this.others > 0) list.push(`另有 ${this.others} 次别的问题`)
    return list
  }

  toJSON(): SavedNotes {
    return { items: this.items.map((n) => ({ ...n })), others: this.others }
  }

  static from(saved: unknown): Notes {
    const notes = new Notes()
    const o = (saved ?? {}) as Partial<SavedNotes>
    if (Array.isArray(o.items)) {
      for (const n of o.items.slice(0, MAX_NOTES - 1)) {
        const text = typeof n?.text === 'string' ? clipNote(n.text) : ''
        if (text) notes.items.push({ text, count: Math.max(1, Math.floor(num(n.count))) })
      }
    }
    notes.others = Math.max(0, Math.floor(num(o.others)))
    return notes
  }
}

interface SavedNotes { items: { text: string; count: number }[]; others: number }

/** 空白并成一个，截到 MAX_NOTE_LENGTH 个字（按字符截，不把一个字截成两半） */
function clipNote(text: string): string {
  const t = text.replace(/\s+/g, ' ').trim()
  const chars = Array.from(t)
  return chars.length > MAX_NOTE_LENGTH ? chars.slice(0, MAX_NOTE_LENGTH).join('') : t
}

/** 画面走哪条线路（和 RemoteClient 的 LineState 一样）；connecting = 选了快速线路、它还没接上，这时没有画面 */
export type ViewerLine = 'relay' | 'connecting' | 'realtime'

/** 控制端此刻的状态：RemoteClient 每秒给一次，变了的时候也马上给 */
export interface ViewerState {
  /** 页面在前台 */
  visible: boolean
  /** 连着对方电脑：中继连接通着，对方的程序也在 */
  connected: boolean
  line: ViewerLine
}

/** 这个浏览器和屏幕：只有品牌和大版本、系统名，不报完整的 User-Agent */
export interface ViewerEnvironment {
  browser: string
  platform: string
  screenWidth: number
  screenHeight: number
  devicePixelRatio: number
  hardwareConcurrency: number
}

/** 存到 sessionStorage 里的（刷新页面接着同一次远程时接着记） */
interface SavedTelemetry {
  startup?: ViewerStartupTelemetry | null
  playback?: SavedPlayback
  v: 1
  decodedFps: SavedSeries
  jitterBufferMs: SavedSeries
  lossPermille: SavedSeries
  lineProblems: SavedNotes
  errors: SavedNotes
  totalMs: number
  relayMs: number
  realtimeMs: number
  freezes: number
  longestFreezeMs: number
  freezeMsTotal: number
  keyFrameRequests: number
  maxFpsCaps: number
  minMaxFps: number
  realtimeReconnects: number
}

type SavedPlayback = Omit<ViewerPlaybackTelemetry, 'fastDecodeMs' | 'relayDecodeQueue'> & {
  fastDecodeMs: SavedSeries; relayDecodeQueue: SavedSeries
}

function savedCount(n: unknown): number | null {
  return typeof n === 'number' && Number.isFinite(n) && n >= 0 ? Math.min(2_147_483_647, Math.floor(n)) : null
}

function addCount(total: number | null, sample: number | null): number | null {
  const n = savedCount(sample)
  return n === null ? total : Math.min(2_147_483_647, (total ?? 0) + n)
}

/**
 * 一次远程里控制端这边的汇总。
 * 卡住：页面在前台、连着对方、画面在一条线路上出着的时候，相邻两帧隔了 FREEZE_MS 以上，而且是这边的问题——
 * 要么帧晚到了（按帧在对方电脑上的时间算，这边多等了 LATE_MS 以上：网络堵、解码跟不上），
 * 要么这期间这边要过关键帧（丢了帧、解码出错、卡在补不回来的包上：中间那些帧画不出来）。
 * 对方屏幕不动时本来就没有新帧，不算；页面在后台、断线、换线路的时候不看，重新开始看以后从出第一帧算起。
 * 卡了多久从"本来该出画面"的时候算，不从上一帧算（屏幕不动时上一帧可能是一分钟前的）：
 * 晚到的按"上一帧之后对方停了多久"往后推（对方停着的那段不算），要过关键帧的最早从第一次要之前 KEY_LOOKBACK_MS 算起。
 */
export class ViewerTelemetry {
  readonly startup = new ViewerStartup(() => this.revision++)
  private fastFramesDecoded: number | null = null
  private fastFramesDropped: number | null = null
  private fastNackRequests: number | null = null
  private fastRetransmittedPackets: number | null = null
  private fastDecodeMs = new Series()
  private relayDecodeQueue = new Series()
  private relaySkippedUntilKey: number | null = null
  private decodedFps = new Series()
  private jitterBufferMs = new Series()
  private lossPermille = new Series()
  private lineProblems = new Notes()
  private errors = new Notes()
  private totalMs = 0
  private relayMs = 0
  private realtimeMs = 0
  private freezes = 0
  private longestFreezeMs = 0
  private freezeMsTotal = 0
  private keyFrameRequests = 0
  private maxFpsCaps = 0
  private minMaxFps = 0
  private realtimeReconnects = 0
  /** 每记一笔加一：和上次报的时候一样就是什么都没变（中途那份可以不报） */
  revision = 0

  // ---- 下面这些不存（刷新页面后从头看） ----
  private state: ViewerState = { visible: true, connected: false, line: 'relay' }
  /** 页面在前台、连着对方、画面在一条线路上：这时候才看卡没卡 */
  private watching = false
  private watchingSince = 0
  /** 记到哪个时刻了（时长）；没在跑（还没开始、RemoteClient 已经关了）时是 null */
  private accountedAt: number | null = null
  private lastTickAt: number | null = null
  private shownThisSecond = 0
  /** 对方最近两次统计里说它那一秒发了几帧 */
  private agentFps = 0
  private agentFpsBefore = 0
  /** 上一帧画出来的时刻、它在对方电脑上的时间；重新开始看以后还没出第一帧时是 null */
  private lastFrameAt: number | null = null
  private lastMediaMs: number | null = null
  /** 上一帧之后第一次要关键帧的时刻；出了帧、不看了就清掉 */
  private firstKeyAt: number | null = null
  private lastCap = 0

  /** 记过东西（RemoteClient 跑起来过）：没有的话不用报 */
  get started(): boolean {
    return this.accountedAt !== null || this.totalMs > 0 || this.startup.started
  }

  /** 状态变了（页面切到前台 / 后台、连上 / 断开、换了线路）：马上调，不用等下一秒 */
  update(now: number, s: ViewerState): void {
    this.account(now)
    const watching = s.visible && s.connected && s.line !== 'connecting'
    const changed = watching !== this.watching || s.line !== this.state.line
    this.state = { ...s }
    if (!changed) return
    // 不看了，或者换了线路（两条线路上帧的时间对不上）：正停着的画面算到这里为止；之后从出第一帧重新算
    if (this.watching) this.cut(now)
    this.watching = watching
    this.watchingSince = now
    this.lastFrameAt = null
    this.lastMediaMs = null
  }

  /** 每秒一次（RemoteClient 的统计定时器）：这一秒画出了几帧，记一个解码帧率样本 */
  tick(now: number, s: ViewerState): void {
    const last = this.lastTickAt
    // 这一秒一直在看，对方也一直在发：屏幕不动时对方不发，那时的 0 帧不算；刚动起来的那一秒只有几帧，也不算（对方上一秒还没在发）。
    // 定时器被压住、隔得太久的不算
    const sending = this.agentFps > 0 && this.agentFpsBefore > 0
    if (last !== null && now - last >= 500 && now - last <= 2000 && this.watching && this.watchingSince <= last && sending) {
      this.decodedFps.add((this.shownThisSecond * 1000) / (now - last))
      this.revision++
    }
    this.shownThisSecond = 0
    this.lastTickAt = now
    this.update(now, s)
  }

  /** RemoteClient 关了（会话结束、被别的窗口顶掉、离开页面）：正停着的画面算到这里为止，时长也停在这里 */
  stop(now: number): void {
    this.update(now, { ...this.state, connected: false })
    this.accountedAt = null
    this.lastTickAt = null
  }

  /** 画出了一帧。mediaMs：这一帧在对方电脑上的时间（毫秒；中继线路上是帧的时间戳，快速线路上由 RTP 时间戳换算），不知道是 null */
  frame(now: number, mediaMs: number | null): void {
    this.shownThisSecond++
    if (!this.watching) return
    const last = this.lastFrameAt
    if (last !== null && now - last > FREEZE_MS) {
      const gap = now - last
      const mediaGap = mediaMs !== null && this.lastMediaMs !== null ? mediaMs - this.lastMediaMs : null
      const late = mediaGap !== null && mediaGap >= 0 ? gap - mediaGap : 0
      // 晚到的：上一帧之后对方停了多久（屏幕不动、没有新帧）不算，只算这一帧本来该到之后又等了多久
      const idle = mediaGap !== null && mediaGap >= 0 ? Math.max(0, mediaGap - FRAME_MS) : 0
      const keyFrom = this.keyStallFrom(last)
      const ms = Math.max(late > LATE_MS ? gap - idle : 0, keyFrom !== null ? now - keyFrom : 0)
      if (ms > FREEZE_MS) this.freeze(ms)
    }
    this.lastFrameAt = now
    this.lastMediaMs = mediaMs
    this.firstKeyAt = null
  }

  /** 对方电脑的每秒统计到了：它这一秒发了几帧 */
  agentStats(sentFps: number): void {
    this.agentFpsBefore = this.agentFps
    this.agentFps = num(sentFps)
  }

  /** 这边向对方要了一个关键帧（丢了帧、解码出错、快速线路上卡在补不回来的包上） */
  keyFrameRequest(now: number): void {
    this.keyFrameRequests++
    this.firstKeyAt ??= now
    this.revision++
  }

  /** 这边请对方每秒最多发几帧（0 = 不限）：往下压的那几次记下来，和压到的最低 */
  maxFps(cap: number): void {
    if (cap > 0 && (this.lastCap === 0 || cap < this.lastCap)) {
      this.maxFpsCaps++
      this.minMaxFps = this.minMaxFps ? Math.min(this.minMaxFps, cap) : cap
      this.revision++
    }
    this.lastCap = Math.max(0, cap)
  }

  /** 快速线路这一秒收得怎么样（发给被控端的那份回报） */
  realtimeReport(r: { jitterBufferMs: number; lossPermille: number }): void {
    this.jitterBufferMs.add(r.jitterBufferMs)
    this.lossPermille.add(r.lossPermille)
    this.revision++
  }

  fastPlayback(delta: FastPlaybackDelta): void {
    this.fastFramesDecoded = addCount(this.fastFramesDecoded, delta.decoded)
    this.fastFramesDropped = addCount(this.fastFramesDropped, delta.dropped)
    this.fastNackRequests = addCount(this.fastNackRequests, delta.nack)
    this.fastRetransmittedPackets = addCount(this.fastRetransmittedPackets, delta.retransmitted)
    if (delta.decodeMs !== null) this.fastDecodeMs.add(delta.decodeMs)
    this.revision++
  }

  relayQueue(size: number): void {
    this.relayDecodeQueue.add(size)
    this.relaySkippedUntilKey ??= 0
    this.revision++
  }

  relaySkippedFrame(): void {
    this.relaySkippedUntilKey = addCount(this.relaySkippedUntilKey, 1)
    this.revision++
  }

  private playbackCounts() {
    return {
      fastFramesDecoded: this.fastFramesDecoded, fastFramesDropped: this.fastFramesDropped,
      fastNackRequests: this.fastNackRequests, fastRetransmittedPackets: this.fastRetransmittedPackets,
      relaySkippedUntilKey: this.relaySkippedUntilKey,
    }
  }

  /** 快速线路接不上 / 断了；retrying：过一会儿会重新接 */
  realtimeFailed(reason: string, retrying: boolean): void {
    this.lineProblems.add(`快速线路：${reason}`)
    if (retrying) this.realtimeReconnects++
    this.revision++
  }

  /** 用户点了"重试"，再接一轮快速线路 */
  realtimeRetried(): void {
    this.realtimeReconnects++
    this.revision++
  }

  /** 中继连接（标准线路，也是键鼠走的那条）断了，正在重连 */
  relayDropped(): void {
    this.lineProblems.add('标准线路：连接断开，重新连接')
    this.revision++
  }

  error(text: string): void {
    this.errors.add(text)
    this.revision++
  }

  /** 到现在为止的汇总。正停着的画面（已经算得上卡住的）也算进去，但不记下来：之后出了帧再按实际的算 */
  snapshot(now: number, env: ViewerEnvironment, final: boolean): ViewerSessionTelemetry {
    let { freezes, longestFreezeMs, freezeMsTotal, totalMs, relayMs, realtimeMs } = this
    const last = this.lastFrameAt
    const keyFrom = this.watching && last !== null ? this.keyStallFrom(last) : null
    if (keyFrom !== null && now - keyFrom > FREEZE_MS) {
      freezes++
      longestFreezeMs = Math.max(longestFreezeMs, now - keyFrom)
      freezeMsTotal += now - keyFrom
    }
    const pending = this.accountedAt !== null ? now - this.accountedAt : 0
    if (pending > 0 && pending <= MAX_GAP_MS) {
      totalMs += pending
      if (this.state.connected && this.state.line === 'relay') relayMs += pending
      else if (this.state.connected && this.state.line === 'realtime') realtimeMs += pending
    }
    const optional = (s: Series): TelemetryStat | null => (s.samples ? s.stat() : null)
    return {
      final,
      startup: this.startup.snapshot(now),
      browser: env.browser,
      platform: env.platform,
      screenWidth: Math.round(num(env.screenWidth)),
      screenHeight: Math.round(num(env.screenHeight)),
      devicePixelRatio: Math.round(num(env.devicePixelRatio) * 100) / 100,
      hardwareConcurrency: Math.round(num(env.hardwareConcurrency)),
      seconds: Math.round(totalMs / 1000),
      relaySeconds: Math.round(relayMs / 1000),
      realtimeSeconds: Math.round(realtimeMs / 1000),
      decodedFps: this.decodedFps.stat(),
      freezes,
      longestFreezeMs: Math.round(longestFreezeMs),
      freezeMsTotal: Math.round(freezeMsTotal),
      keyFrameRequests: this.keyFrameRequests,
      maxFpsCaps: this.maxFpsCaps,
      minMaxFps: this.minMaxFps,
      realtimeReconnects: this.realtimeReconnects,
      lineProblems: this.lineProblems.list(),
      jitterBufferMs: optional(this.jitterBufferMs),
      lossPermille: optional(this.lossPermille),
      playback: { ...this.playbackCounts(), fastDecodeMs: optional(this.fastDecodeMs), relayDecodeQueue: optional(this.relayDecodeQueue) },
      errors: this.errors.list(),
    }
  }

  toJSON(): SavedTelemetry {
    return this.save(performance.now())
  }

  save(now: number): SavedTelemetry {
    return {
      v: 1,
      startup: this.startup.snapshot(now),
      playback: { ...this.playbackCounts(), fastDecodeMs: this.fastDecodeMs.toJSON(), relayDecodeQueue: this.relayDecodeQueue.toJSON() },
      decodedFps: this.decodedFps.toJSON(),
      jitterBufferMs: this.jitterBufferMs.toJSON(),
      lossPermille: this.lossPermille.toJSON(),
      lineProblems: this.lineProblems.toJSON(),
      errors: this.errors.toJSON(),
      totalMs: this.totalMs,
      relayMs: this.relayMs,
      realtimeMs: this.realtimeMs,
      freezes: this.freezes,
      longestFreezeMs: this.longestFreezeMs,
      freezeMsTotal: this.freezeMsTotal,
      keyFrameRequests: this.keyFrameRequests,
      maxFpsCaps: this.maxFpsCaps,
      minMaxFps: this.minMaxFps,
      realtimeReconnects: this.realtimeReconnects,
    }
  }

  /** 刷新页面前存下来的（toJSON）接着记；不认识的返回 null */
  static restore(saved: unknown): ViewerTelemetry | null {
    if (!saved || typeof saved !== 'object' || (saved as { v?: unknown }).v !== 1) return null
    const o = saved as Partial<SavedTelemetry>
    const t = new ViewerTelemetry()
    t.startup.restore(o.startup)
    const p = o.playback
    t.fastFramesDecoded = savedCount(p?.fastFramesDecoded)
    t.fastFramesDropped = savedCount(p?.fastFramesDropped)
    t.fastNackRequests = savedCount(p?.fastNackRequests)
    t.fastRetransmittedPackets = savedCount(p?.fastRetransmittedPackets)
    t.relaySkippedUntilKey = savedCount(p?.relaySkippedUntilKey)
    t.fastDecodeMs = Series.from(p?.fastDecodeMs)
    t.relayDecodeQueue = Series.from(p?.relayDecodeQueue)
    t.decodedFps = Series.from(o.decodedFps)
    t.jitterBufferMs = Series.from(o.jitterBufferMs)
    t.lossPermille = Series.from(o.lossPermille)
    t.lineProblems = Notes.from(o.lineProblems)
    t.errors = Notes.from(o.errors)
    const count = (v: unknown): number => Math.max(0, Math.floor(num(v)))
    t.totalMs = Math.max(0, num(o.totalMs))
    t.relayMs = Math.max(0, num(o.relayMs))
    t.realtimeMs = Math.max(0, num(o.realtimeMs))
    t.freezes = count(o.freezes)
    t.longestFreezeMs = Math.max(0, num(o.longestFreezeMs))
    t.freezeMsTotal = Math.max(0, num(o.freezeMsTotal))
    t.keyFrameRequests = count(o.keyFrameRequests)
    t.maxFpsCaps = count(o.maxFpsCaps)
    t.minMaxFps = count(o.minMaxFps)
    t.realtimeReconnects = count(o.realtimeReconnects)
    return t
  }

  /** 把时间记到上一个状态上：总时长，画面在中继 / 快速线路上的时长 */
  private account(now: number): void {
    const at = this.accountedAt
    this.accountedAt = now
    if (at === null) return
    const dt = now - at
    if (dt <= 0 || dt > MAX_GAP_MS) return
    this.totalMs += dt
    if (this.state.connected && this.state.line === 'relay') this.relayMs += dt
    else if (this.state.connected && this.state.line === 'realtime') this.realtimeMs += dt
  }

  /** 不看了：画面要是正停着、而且这期间要过关键帧（画不出来的那种），算到这里为止 */
  private cut(now: number): void {
    const last = this.lastFrameAt
    const keyFrom = last !== null ? this.keyStallFrom(last) : null
    if (keyFrom !== null && now - keyFrom > FREEZE_MS) this.freeze(now - keyFrom)
    this.lastFrameAt = null
    this.lastMediaMs = null
    this.firstKeyAt = null
  }

  /** 上一帧（last）之后要过关键帧：这次卡住从哪儿算起（上一帧和第一次要之前 KEY_LOOKBACK_MS 里晚的那个）；没要过是 null */
  private keyStallFrom(last: number): number | null {
    const key = this.firstKeyAt
    return key !== null && key > last ? Math.max(last, key - KEY_LOOKBACK_MS) : null
  }

  private freeze(ms: number): void {
    this.freezes++
    this.longestFreezeMs = Math.max(this.longestFreezeMs, ms)
    this.freezeMsTotal += ms
    this.revision++
  }
}

/** navigator 里用得到的那几样（userAgentData 只有 Chromium 系才有，类型定义里也没有） */
export interface NavigatorLike {
  userAgent: string
  maxTouchPoints?: number
  userAgentData?: { brands?: ReadonlyArray<{ brand: string; version: string }>; platform?: string; mobile?: boolean }
}

/** 认得的品牌（userAgentData.brands 里的名字）→ 报上去的名字 */
const BRANDS: ReadonlyArray<readonly [RegExp, string]> = [
  [/^(?:Microsoft |Headless)Edge$/i, 'Edge'],
  [/^(?:Google |Headless)Chrome$/i, 'Chrome'],
  [/^Opera/i, 'Opera'],
  [/^Brave/i, 'Brave'],
  [/^Samsung Internet/i, 'Samsung Internet'],
  [/^YaBrowser|^Yandex/i, 'Yandex'],
]

/** User-Agent 里认得的浏览器（先认套壳的，最后才是 Chrome / Safari） */
const UA_BROWSERS: ReadonlyArray<readonly [RegExp, string]> = [
  // 前面不要求单词边界：无界面的 Edge 写成 HeadlessEdg/…
  [/Edg(?:e|A|iOS)?\/(\d+)/, 'Edge'],
  [/\bOPR\/(\d+)/, 'Opera'],
  [/\bSamsungBrowser\/(\d+)/, 'Samsung Internet'],
  [/\bFirefox\/(\d+)/, 'Firefox'],
  [/\bFxiOS\/(\d+)/, 'Firefox'],
  [/\bCriOS\/(\d+)/, 'Chrome'],
  [/\bChrome\/(\d+)/, 'Chrome'],
  [/\bVersion\/(\d+)[\d.]* (?:Mobile\/\S+ )?Safari\//, 'Safari'],
]

/** 浏览器品牌 + 大版本（"Edge 142"）、系统（"Windows"）。只取这两样，User-Agent 里别的都不报 */
export function describeBrowser(nav: NavigatorLike): { browser: string; platform: string } {
  const data = nav.userAgentData
  let browser = ''
  const brands = (data?.brands ?? []).filter((b) => b && typeof b.brand === 'string' && !/not.?a.?brand/i.test(b.brand))
  for (const [pattern, name] of BRANDS) {
    const b = brands.find((x) => pattern.test(x.brand))
    if (b) {
      browser = `${name} ${major(b.version)}`.trim()
      break
    }
  }
  if (!browser) {
    for (const [pattern, name] of UA_BROWSERS) {
      const m = pattern.exec(nav.userAgent)
      if (m) {
        browser = `${name} ${m[1]}`
        break
      }
    }
  }
  if (!browser) {
    const other = brands.find((b) => b.brand !== 'Chromium') ?? brands[0]
    browser = other ? `${other.brand} ${major(other.version)}`.trim() : '未知'
  }
  return { browser: clipNote(browser).slice(0, 60), platform: platformOf(nav) }
}

function major(version: string | undefined): string {
  return /^\d+/.exec(version ?? '')?.[0] ?? ''
}

function platformOf(nav: NavigatorLike): string {
  const data = nav.userAgentData
  if (data?.platform) return `${clipNote(data.platform).slice(0, 40)}${data.mobile ? '（手机）' : ''}`
  const ua = nav.userAgent
  if (/Windows/.test(ua)) return 'Windows'
  if (/Android/.test(ua)) return 'Android'
  if (/iPhone|iPod/.test(ua)) return 'iOS'
  if (/iPad/.test(ua) || (/Macintosh/.test(ua) && (nav.maxTouchPoints ?? 0) > 1)) return 'iPadOS'
  if (/Mac OS X|Macintosh/.test(ua)) return 'macOS'
  if (/CrOS/.test(ua)) return 'ChromeOS'
  if (/Linux/.test(ua)) return 'Linux'
  return '未知'
}

/** 这个浏览器和屏幕（在浏览器里取；测试里用不到） */
export function viewerEnvironment(): ViewerEnvironment {
  const { browser, platform } = describeBrowser(navigator as unknown as NavigatorLike)
  return {
    browser,
    platform,
    screenWidth: window.screen?.width ?? 0,
    screenHeight: window.screen?.height ?? 0,
    devicePixelRatio: window.devicePixelRatio || 1,
    hardwareConcurrency: navigator.hardwareConcurrency || 0,
  }
}
