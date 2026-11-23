/** 单次 getStats 中一条接收视频流的增量；缺失的浏览器指标保留为 null。 */
export interface FastPlaybackDelta {
  decoded: number | null
  dropped: number | null
  nack: number | null
  retransmitted: number | null
  decodeMs: number | null
}

interface InboundSample {
  at: number
  decoded: number | null
  dropped: number | null
  nack: number | null
  retransmitted: number | null
  decodePair: { seconds: number; frames: number } | null
}

function value(n: unknown): number | null {
  return typeof n === 'number' && Number.isFinite(n) && n >= 0 ? n : null
}

function increment(current: number | null, previous: number | null | undefined): number | null {
  if (current === null) return null
  return previous != null && current >= previous ? current - previous : current
}

/** 一个 PeerConnection 一份。stats id 只用作本地去重，既不持久化也不上报。 */
export class FastPlaybackCounters {
  private readonly samples = new Map<string, InboundSample>()

  observe(stat: Record<string, unknown>): FastPlaybackDelta | null {
    if (stat.type !== 'inbound-rtp' || stat.kind !== 'video' || typeof stat.id !== 'string') return null
    const at = value(stat.timestamp)
    const old = this.samples.get(stat.id)
    if (at === null || (old && at <= old.at)) return null
    const decoded = value(stat.framesDecoded)
    const dropped = value(stat.framesDropped)
    const nack = value(stat.nackCount)
    const retransmitted = value(stat.retransmittedPacketsReceived)
    const seconds = value(stat.totalDecodeTime)
    const pair = decoded !== null && seconds !== null ? { frames: decoded, seconds } : null
    let decodeMs: number | null = null
    if (pair) {
      const prior = old?.decodePair
      const continuous = prior && prior.frames <= pair.frames && prior.seconds <= pair.seconds
      const frames = pair.frames - (continuous ? prior.frames : 0)
      if (frames > 0) decodeMs = 1000 * (pair.seconds - (continuous ? prior.seconds : 0)) / frames
    }
    this.samples.set(stat.id, {
      at, decoded: decoded ?? old?.decoded ?? null, dropped: dropped ?? old?.dropped ?? null,
      nack: nack ?? old?.nack ?? null, retransmitted: retransmitted ?? old?.retransmitted ?? null,
      decodePair: pair ?? old?.decodePair ?? null,
    })
    return {
      decoded: increment(decoded, old?.decoded), dropped: increment(dropped, old?.dropped),
      nack: increment(nack, old?.nack), retransmitted: increment(retransmitted, old?.retransmitted), decodeMs,
    }
  }
}
