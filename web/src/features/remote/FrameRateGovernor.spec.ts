import { describe, expect, it } from 'vitest'
import { FrameRateGovernor } from './FrameRateGovernor'

interface Scenario {
  seconds: number
  /** 解码器解一帧要多久（在主线程之外） */
  decodeMs: (now: number) => number
  /** 页面主线程排队多久（解出来的帧、收到的消息、探测消息都在这条队里等），默认 2 ms */
  loopMs?: (now: number) => number
  /** 这一刻画面有没有变化（被控端只在有变化时发帧），默认一直在变 */
  moving?: (now: number) => boolean
  /** 网络一次性把攒着的帧一起送到，每隔这么多毫秒一批（TCP 丢包重传后常见） */
  burstEveryMs?: number
  /** 被控端说它按多少帧发 */
  advertised?: number
}

/**
 * 模拟中继线路上的一台控制端，和真的被控端一样"确认回来才接着发"：被控端按每秒 advertised 帧（或者这边请求的上限）排时间，
 * 但没确认的帧到了 8 帧就先不发（在途上限）；这边解码器一帧一帧排着解，解完的帧要等主线程轮到它才画、才确认。
 * 每 100 ms 探测一次（主线程排了多久、解码器里压着几帧），每 500 ms 问一次 governor，它说改上限就照改。
 */
function simulate(s: Scenario) {
  const g = new FrameRateGovernor()
  const advertised = s.advertised ?? 30
  const loopMs = s.loopMs ?? (() => 2)
  const moving = s.moving ?? (() => true)
  const changes: { at: number; cap: number }[] = []
  const network: { ts: number }[] = []
  const decodeQueue: { ts: number }[] = []
  let ts = 0
  let decoding: { done: number; ts: number } | null = null
  const callbacks: { at: number; ts: number }[] = []
  let outstanding = 0
  let nextSend = 0
  const shown: number[] = []
  for (let now = 0; now <= s.seconds * 1000; now++) {
    if (now >= nextSend) {
      nextSend += 1000 / (g.cap || advertised)
      if (moving(now) && outstanding < 8) {
        outstanding++
        network.push({ ts: ++ts })
      }
    }
    if (!s.burstEveryMs || now % s.burstEveryMs === 0) {
      while (network.length) {
        const f = network.shift()!
        g.received(f.ts, now, decodeQueue.length + (decoding ? 1 : 0))
        decodeQueue.push(f)
      }
    }
    if (decoding && decoding.done <= now) {
      callbacks.push({ at: now + loopMs(now), ts: decoding.ts })
      decoding = null
    }
    if (!decoding && decodeQueue.length) {
      const f = decodeQueue.shift()!
      decoding = { done: now + s.decodeMs(now), ts: f.ts }
    }
    while (callbacks.length && callbacks[0]!.at <= now) {
      const c = callbacks.shift()!
      g.decoded(c.ts, now)
      shown.push(now)
      outstanding--
    }
    if (now % 100 === 0) g.probe(now, loopMs(now), decodeQueue.length + (decoding ? 1 : 0))
    if (now % 500 === 0) {
      const cap = g.tick(now, advertised)
      if (cap !== null) changes.push({ at: now, cap })
    }
  }
  const tailFps = shown.filter((t) => t > (s.seconds - 5) * 1000).length / 5
  return { g, changes, tailFps }
}

interface Moment {
  /** 主线程这一次排了多久 */
  loopMs: number
  /** 解码器里压着几帧 */
  queued: number
  /** 这一刻每秒解出几帧 */
  fps: number
}

/**
 * 不模拟收发，直接按给定的"这一刻什么样"喂给 governor：每 100 ms 探测一次（probeEveryMs 可改），按 fps 记解出来的帧，
 * 每 500 ms 问一次。不调 received，所以量不到解一帧要多久（只看探测和出帧）。
 */
function drive(g: FrameRateGovernor, from: number, to: number, at: (now: number) => Moment, probeEveryMs = 100) {
  const changes: { at: number; cap: number }[] = []
  let nextOutput = from
  for (let now = from; now < to; now++) {
    const m = at(now)
    if (m.fps > 0 && now >= nextOutput) {
      g.decoded(now, now)
      nextOutput = now + 1000 / m.fps
    }
    if (now % probeEveryMs === 0) g.probe(now, m.loopMs, m.queued)
    if (now % 500 === 0) {
      const cap = g.tick(now, 30)
      if (cap !== null) changes.push({ at: now, cap })
    }
  }
  return changes
}

const behindAt = (fps: number): Moment => ({ loopMs: 100, queued: 0, fps })
const fineAt = (fps: number): Moment => ({ loopMs: 30, queued: 0, fps })
const easyAt = (fps: number): Moment => ({ loopMs: 2, queued: 0, fps })

describe('控制端跟不上时请被控端少发几帧', () => {
  it('跟得上（每帧 10 ms）：一直不限', () => {
    const r = simulate({ seconds: 30, decodeMs: () => 10 })
    expect(r.changes).toEqual([])
    expect(r.tailFps).toBeGreaterThanOrEqual(29)
  })

  it('每帧要解 40 ms（最多 25 帧）：几秒内降到二十上下，之后不再一级一级往下掉', () => {
    const r = simulate({ seconds: 30, decodeMs: () => 40 })
    expect(r.changes[0]!.at).toBeLessThanOrEqual(4000)
    expect(r.changes[0]!.cap).toBeGreaterThanOrEqual(18)
    expect(r.changes[0]!.cap).toBeLessThanOrEqual(24)
    expect(r.g.cap).toBeGreaterThanOrEqual(17)
    // 照上限出帧，不再排队
    expect(r.tailFps).toBeGreaterThanOrEqual(r.g.cap * 0.9)
  })

  it('每帧要解 80 ms（最多 12.5 帧）：降到十帧上下，量得到解一帧要多久，就不往它解不过来的帧率上试', () => {
    const r = simulate({ seconds: 120, decodeMs: () => 80 })
    expect(r.changes[0]!.cap).toBeGreaterThanOrEqual(8)
    expect(r.changes[0]!.cap).toBeLessThanOrEqual(11)
    expect(r.changes.filter((c) => c.at > 10_000)).toEqual([])
  })

  it('一直跟不上：往上试过以后又会退回来，不会一路放开', () => {
    const r = simulate({ seconds: 120, decodeMs: () => 40 })
    expect(r.g.cap).toBeGreaterThan(0)
    for (const c of r.changes) expect(c.cap >= 15 && c.cap <= 25).toBe(true)
  })

  it('设备后来不忙了（每帧 40 ms → 10 ms）：一步一步放开，最后不限', () => {
    const r = simulate({ seconds: 90, decodeMs: (now) => (now < 10_000 ? 40 : 10) })
    expect(r.changes[0]!.cap).toBeLessThan(30)
    expect(r.g.cap).toBe(0)
  })

  it('降下来以后画面大多时候不动（十秒里只动两秒）：静止的时候也算轻松，照样一步一步放开', () => {
    const r = simulate({
      seconds: 150,
      decodeMs: (now) => (now < 10_000 ? 40 : 10),
      moving: (now) => now < 10_000 || now % 10_000 < 2_000,
    })
    expect(r.changes[0]!.cap).toBeLessThan(30)
    expect(r.g.cap).toBe(0)
  })

  it('页面主线程忙不过来（每次排 80 ms）、解码器不慢：照样看得出来', () => {
    const r = simulate({ seconds: 20, decodeMs: () => 5, loopMs: () => 80 })
    expect(r.changes.length).toBeGreaterThanOrEqual(1)
    expect(r.g.cap).toBeGreaterThan(0)
  })

  it('画面不动的时候页面忙（别的事）：不关被控端的事，不限', () => {
    const r = simulate({ seconds: 20, decodeMs: () => 5, loopMs: () => 200, moving: () => false })
    expect(r.changes).toEqual([])
  })

  it('网络一次送来一批帧（每 267 ms 八帧）、解码器够快：排一会儿就清空，不算跟不上', () => {
    const r = simulate({ seconds: 30, decodeMs: () => 25, burstEveryMs: 267 })
    expect(r.changes).toEqual([])
  })

  it('照被控端说的帧率算：老版本按 20 帧发，往上放开到 20 就算不限了', () => {
    // 每 15 秒放开两成：11 → 13 → 16 → 19 → 不限，要一分多钟
    const r = simulate({ seconds: 90, decodeMs: (now) => (now < 10_000 ? 70 : 10), advertised: 20 })
    expect(r.changes[0]!.cap).toBeLessThan(20)
    for (const c of r.changes) expect(c.cap).toBeLessThan(20)
    expect(r.g.cap).toBe(0)
  })

  it('主线程一直排着 60 ms 的队：不管按多少帧发都算跟不上（被控端看到确认晚 60 ms 以上就当网络堵）', () => {
    const at20 = simulate({ seconds: 15, decodeMs: () => 5, loopMs: () => 60, advertised: 20 })
    expect(at20.changes.length).toBeGreaterThanOrEqual(1)
    const quiet = simulate({ seconds: 15, decodeMs: () => 5, loopMs: () => 30, advertised: 30 })
    expect(quiet.changes).toEqual([])
  })

  it('刚建好解码器的这一阵先不下结论', () => {
    const g = new FrameRateGovernor()
    g.hold(0, 2000)
    for (let now = 0; now <= 1900; now += 100) {
      g.probe(now, 300, 6)
      g.decoded(now, now)
      if (now % 500 === 0) expect(g.tick(now, 30)).toBeNull()
    }
    expect(g.cap).toBe(0)
  })

  it('跟不上不到一秒就缓过来（一阵一阵的）：不改', () => {
    // 每 2 秒里有 1.4 秒在排长队：只有一次判断时整一秒都在排队，不够连续一秒
    const g = new FrameRateGovernor()
    const changes = drive(g, 0, 30_000, (now) => (now % 2000 >= 100 && now % 2000 <= 1500 ? behindAt(20) : fineAt(20)))
    expect(changes).toEqual([])
  })

  it('探测太少（页面在后台，定时器一秒才跑一次）：不下结论', () => {
    const g = new FrameRateGovernor()
    const changes = drive(g, 0, 20_000, () => ({ loopMs: 300, queued: 0, fps: 20 }), 1000)
    expect(changes).toEqual([])
  })

  it('降过一次以后，排着的帧消下去之前（最多 10 秒）不接着往下降', () => {
    const g = new FrameRateGovernor()
    const changes = drive(g, 0, 20_000, () => behindAt(20))
    expect(changes.length).toBeGreaterThanOrEqual(2)
    expect(changes[1]!.at - changes[0]!.at).toBeGreaterThanOrEqual(10_000)
  })

  it('在哪个帧率上跟不上过，两分钟里往上放开时不再试到那么高', () => {
    const g = new FrameRateGovernor()
    // 30 帧跟不上 → 17；缓一下；17 帧也跟不上 → 12
    const first = drive(g, 0, 3000, () => behindAt(20))
    drive(g, 3000, 4000, () => fineAt(17))
    const second = drive(g, 4000, 7000, () => behindAt(15))
    expect(first.map((c) => c.cap)).toEqual([17])
    expect(second.map((c) => c.cap)).toEqual([12])
    const failedAt17 = second[0]!.at
    // 之后一直很轻松：两分钟里最多放到 16（比 17 低一帧），过了两分钟才再往上试
    const later = drive(g, 7000, 200_000, () => easyAt(12))
    const within = later.filter((c) => c.at - failedAt17 <= 120_000)
    expect(within.length).toBeGreaterThanOrEqual(1)
    for (const c of within) expect(c.cap).toBeGreaterThan(0)
    for (const c of within) expect(c.cap).toBeLessThanOrEqual(16)
    expect(later.some((c) => c.at - failedAt17 > 120_000 && (c.cap === 0 || c.cap > 16))).toBe(true)
  })

  it('页面在后台待了一小时（只记不判断）：记录不会一直攒着，回来第一次判断很快', () => {
    const g = new FrameRateGovernor()
    for (let now = 0; now < 3_600_000; now += 33) {
      g.received(now, now, 0)
      g.decoded(now, now + 5)
      if (now % 99 === 0) g.probe(now, 2, 0)
    }
    const inner = g as unknown as { outputs: unknown[]; probes: unknown[]; services: unknown[]; arrivals: Map<number, number> }
    expect(inner.outputs.length).toBeLessThanOrEqual(40)
    expect(inner.probes.length).toBeLessThanOrEqual(12)
    expect(inner.services.length).toBeLessThanOrEqual(40)
    expect(inner.arrivals.size).toBeLessThanOrEqual(1)
    const started = performance.now()
    g.tick(3_600_000, 30)
    expect(performance.now() - started).toBeLessThan(50)
  })

  it('从头来：限过的要告诉对方不限了，没限过的什么都不用说', () => {
    const r = simulate({ seconds: 6, decodeMs: () => 40 })
    expect(r.g.cap).toBeGreaterThan(0)
    expect(r.g.reset()).toBe(0)
    expect(r.g.cap).toBe(0)
    expect(r.g.reset()).toBeNull()
  })
})
