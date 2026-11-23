import type { ViewerFastAttemptTelemetry, ViewerStartupTelemetry } from './telemetry'

type Line = NonNullable<ViewerStartupTelemetry['firstFrameLine']>
type Outcome = ViewerFastAttemptTelemetry['outcome']
const MAX_ATTEMPTS = 8
const elapsed = (start: number | null, end: number): number | null =>
  start !== null && end >= start ? Math.round(end - start) : null
const duration = (v: unknown): number | null => typeof v === 'number' && Number.isSafeInteger(v) && v >= 0 ? v : null

/** 把点击时刻随这一次站内导航交给控制页。刷新或直接打开链接时不能拿新文档的时钟和它相减。 */
export function requestedSessionRoute(id: string, requestedAt: number) {
  return { path: `/remote/${id}`, state: { remoteRequestSession: id, remoteRequestAt: requestedAt, remoteRequestOrigin: performance.timeOrigin } }
}

export function requestTimeInDocument(state: { remoteRequestSession?: unknown; remoteRequestAt?: unknown; remoteRequestOrigin?: unknown } | null, id: string, origin: number): number | null {
  const at = state?.remoteRequestAt
  return state?.remoteRequestSession === id && state.remoteRequestOrigin === origin && typeof at === 'number' && Number.isFinite(at) && at >= 0 ? at : null
}

/** 页面内用同一个单调时钟，向服务器和刷新前的快照只交付时长。 */
export class ViewerStartup {
  private requestedAt: number | null = null
  // undefined 表示还没连接，null 表示首次连接发生在刷新前的文档里。
  private connectedAt: number | null | undefined
  private firstLine: Line | null = null
  private requestMs: number | null = null
  private connectMs: number | null = null
  private attempts: ViewerFastAttemptTelemetry[] = []
  private pending: { attempt: number; start: number; requested: boolean; pull: number | null } | null = null
  private readonly changed: () => void

  constructor(changed: () => void) { this.changed = changed }

  get started(): boolean { return this.connectedAt !== undefined || this.firstLine !== null || this.attempts.length > 0 }

  requested(at: number | null): void { this.requestedAt ??= at }

  connecting(now: number): void {
    if (this.connectedAt !== undefined || this.firstLine !== null) return
    this.connectedAt = now
    this.changed()
  }

  fastRequested(now: number): void { this.begin(now, true) }

  private begin(now: number, requested: boolean): void {
    this.finish(now, 'cancelled')
    const attempt = (this.attempts.at(-1)?.attempt ?? 0) + 1
    if (this.attempts.length === MAX_ATTEMPTS) this.attempts.shift()
    this.pending = { attempt, start: now, requested, pull: null }
    this.changed()
  }

  pulling(now: number): void {
    // 对端可能重新发布了一代新轨道，这次拉流没有对应的请求时刻。
    if (!this.pending || this.pending.pull !== null) this.begin(now, false)
    this.pending!.pull = now
    this.changed()
  }

  painted(now: number, line: Line): void {
    if (line === 'realtime') this.finish(now, 'firstFrame')
    if (this.firstLine !== null) return
    this.firstLine = line
    this.requestMs = elapsed(this.requestedAt, now)
    this.connectMs = elapsed(this.connectedAt ?? null, now)
    this.changed()
  }

  finish(now: number, outcome: 'failed' | 'cancelled' | 'firstFrame'): void {
    if (!this.pending) return
    this.attempts.push(this.measure(now, outcome))
    this.pending = null
    this.changed()
  }

  private measure(now: number, outcome: Outcome): ViewerFastAttemptTelemetry {
    const { attempt, start, requested, pull } = this.pending!
    return { attempt, outcome, totalMs: elapsed(start, now) ?? 0,
      publisherWaitMs: requested ? elapsed(start, pull ?? now) : null,
      pullToFirstFrameMs: elapsed(pull, now) }
  }

  snapshot(now: number): ViewerStartupTelemetry | null {
    if (!this.started && !this.pending) return null
    const attempts = this.attempts.map(a => ({ ...a }))
    if (this.pending) attempts.push(this.measure(now, 'pending'))
    return { firstFrameLine: this.firstLine, requestToFirstFrameMs: this.requestMs, connectToFirstFrameMs: this.connectMs, attempts }
  }

  restore(saved?: ViewerStartupTelemetry | null): void {
    if (!saved) return
    this.connectedAt = null
    if (saved.firstFrameLine === 'relay' || saved.firstFrameLine === 'realtime') {
      this.firstLine = saved.firstFrameLine
      this.requestMs = duration(saved.requestToFirstFrameMs)
      this.connectMs = duration(saved.connectToFirstFrameMs)
    }
    if (!Array.isArray(saved.attempts)) return
    for (const a of saved.attempts.slice(-MAX_ATTEMPTS)) {
      if (!a || !Number.isSafeInteger(a.attempt) || a.attempt <= (this.attempts.at(-1)?.attempt ?? 0)
        || !['firstFrame', 'failed', 'cancelled', 'pending'].includes(a.outcome)) continue
      this.attempts.push({ attempt: a.attempt, outcome: a.outcome === 'pending' ? 'cancelled' : a.outcome,
        totalMs: duration(a.totalMs) ?? 0, publisherWaitMs: duration(a.publisherWaitMs),
        pullToFirstFrameMs: duration(a.pullToFirstFrameMs) })
    }
  }
}
