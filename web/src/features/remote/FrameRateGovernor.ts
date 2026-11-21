/** 统计最近这么久 */
const WINDOW_MS = 1_000
/** 这一秒里至少有这么多次探测，才下结论（页面在后台、定时器被压住时不够） */
const MIN_PROBES = 3
/** 连续这么久都跟不上，才请对方少发（偶尔卡一下不算） */
const BEHIND_FOR_MS = 1_000
/**
 * 页面主线程一直排着队：这一秒里排队最短的一次也超过这么久。不随帧率放宽：
 * 被控端看到确认晚了 60 ms 以上就当网络堵，这边排的队一直比这个长，不管帧率多低都得再少发
 */
const BEHIND_LOOP_MS = 50
/** 解码器一直压着帧：这一秒里最空的时候也还排着这么多帧 */
const BEHIND_QUEUE = 2
/** 跟不上时这一秒里至少解出这么多帧才动手（画面不动时页面忙是别的原因，不关画面的事） */
const MIN_OUTPUTS = 5
/** 请对方按自己实际解出来的速度再打个折，留点余地 */
const BEHIND_SHARE = 0.85
const MIN_FPS = 5
/** 改了上限以后，排着的帧要一会儿才消得下去：消下去之前（最多这么久）不再往下改 */
const SETTLE_MAX_MS = 10_000
/** 一直很轻松这么久，往上放开一点：主线程几乎不排队、解码器里最多一帧 */
const EASY_FOR_MS = 15_000
const EASY_LOOP_MS = 20
/** 每次往上放开两成（至少 1 帧）：帧率低的时候 5 帧就是一大步，一步就会迈过它的本事 */
const STEP_UP_SHARE = 0.2
/** 在哪个帧率上跟不上过，记这么久：往上放开时不再试到那么高（不然每隔十几秒就去撞一次墙） */
const REMEMBER_MS = 120_000
/** 往上放开前看一眼解一帧要多久：按新的帧率还得留出这么多余地（解一帧 ≤ 帧间隔的八成），不然一放开就又跟不上 */
const STEP_SLACK = 0.8
/** 解一帧用不到帧间隔的一半：很宽裕，以前在哪儿跟不上过已经不作数了（设备不忙了） */
const HEADROOM_SHARE = 0.5
/** 收到了但一直没解出来的帧（解码器被换掉了之类）：这么久以后就不记了 */
const FORGET_MS = 5_000
/** 每样记录最多留这么多条（正常一秒几十条；页面在后台时不判断，也不能一直攒着） */
const MAX_RECORDS = 300

/**
 * 控制端自己跟不跟得上（中继线路）。
 * 中继线路上被控端靠"这边解出来才确认"控制发多快。这边解码慢、页面忙不过来时，确认会越来越晚，
 * 被控端看起来和网络堵了一样，会把码率越压越低、画面越来越糊——可帧变小了这边照样解不过来。
 * 所以由这边自己看，只看本机的两样东西（网络慢不算在里面）：
 * - 页面主线程排队多久：每 100 ms 往自己的消息队列里投一条，看它等了多久才轮到（解出来的帧、收到的消息都在这条队里排）；
 * - 解码器里压着几帧。
 * 这一秒里"最好的时候"都在排队，才算跟不上（网络抖一下一次来一批帧，排一会儿就清空了，不算）。
 * 连续 1 秒跟不上，就请被控端每秒最多发自己这一秒实际解出来的八五成（MaxFps）；之后一直很轻松，再一步一步放开，直到不限。
 */
export class FrameRateGovernor {
  /** 现在请对方每秒最多几帧；0 = 不限 */
  cap = 0
  private readonly outputs: number[] = []
  /** 送进解码器时它是空着的帧：收到的时刻（解出来时就知道解一帧要多久） */
  private readonly arrivals = new Map<number, number>()
  private readonly services: { at: number; ms: number }[] = []
  /** 最近量到的解一帧要多久（中位数，ms）；没量到过是 null */
  private serviceMs: number | null = null
  private readonly probes: { at: number; loopMs: number; queued: number }[] = []
  private behindSince: number | null = null
  private easySince: number | null = null
  private changedAt = Number.NEGATIVE_INFINITY
  /** 刚改过上限、排着的帧还没消下去 */
  private settling = false
  private holdUntil = Number.NEGATIVE_INFINITY
  /** 最近在每秒几帧上跟不上过（0 = 没有） */
  private tooMuch = 0
  private tooMuchAt = Number.NEGATIVE_INFINITY

  /** 收到一帧、交给解码器之前。queued：解码器里已经排着几帧（空着时才拿它量解一帧要多久） */
  received(timestamp: number, now: number, queued: number): void {
    if (queued === 0) this.arrivals.set(timestamp, now)
    if (this.arrivals.size > MAX_RECORDS) this.arrivals.clear()
  }

  /** 解出了一帧（轮到主线程画它的时候） */
  decoded(timestamp: number, now: number): void {
    this.outputs.push(now)
    const at = this.arrivals.get(timestamp)
    if (at !== undefined) {
      this.arrivals.delete(timestamp)
      this.services.push({ at: now, ms: now - at })
    }
    // 边记边清：页面在后台时不判断（tick 不跑），这里不清的话会一直攒下去，回到前台第一次判断要清好久
    this.trim(now)
  }

  /** 每 100 ms 左右一次：主线程这一次排了多久的队、解码器里压着几帧 */
  probe(now: number, loopMs: number, queued: number): void {
    this.probes.push({ at: now, loopMs: Math.max(0, loopMs), queued })
    this.trim(now)
  }

  /** 这么久里先别下结论：解码器刚建好、刚换线路、页面刚回到前台，头一阵本来就慢 */
  hold(now: number, ms: number): void {
    this.holdUntil = Math.max(this.holdUntil, now + ms)
    this.behindSince = null
    this.easySince = null
  }

  /**
   * 每半秒左右调一次。advertised：被控端说它按多少帧发（VideoConfig）。
   * 返回要告诉被控端的新上限（0 = 不限），不用改时返回 null。
   */
  tick(now: number, advertised: number): number | null {
    this.forget(now)
    if (now < this.holdUntil || advertised <= 0 || this.probes.length < MIN_PROBES) return null
    const loops = this.probes.map((p) => p.loopMs).sort((a, b) => a - b)
    const queues = this.probes.map((p) => p.queued)
    const loopMin = loops[0]!
    const loopMedian = loops[Math.floor(loops.length / 2)]!
    const queueMin = Math.min(...queues)
    const queueMax = Math.max(...queues)
    const interval = 1000 / (this.cap || advertised)
    const behind = loopMin > BEHIND_LOOP_MS || queueMin >= BEHIND_QUEUE
    const easy = !behind && loopMedian < EASY_LOOP_MS && queueMax <= 1

    if (behind) {
      this.easySince = null
      this.behindSince ??= now
      if (now - this.behindSince < BEHIND_FOR_MS) return null
      // 刚改过：排着的帧还在消，先等它消下去（消不下去说明还是太多，等够了再往下改）
      if (this.settling && now - this.changedAt < SETTLE_MAX_MS) return null
      // 画面没怎么动：页面忙是别的事，不关这边发多快
      if (this.outputs.length < MIN_OUTPUTS) return null
      // 这一秒实际解出来几帧，就是这台设备现在的本事
      const managed = Math.floor(this.outputs.length * (1000 / WINDOW_MS) * BEHIND_SHARE)
      const was = this.cap || advertised
      const next = Math.max(MIN_FPS, Math.min(was - 1, managed))
      if (this.cap && next >= this.cap) return null
      if (!this.tooMuch || was < this.tooMuch) this.tooMuch = was
      this.tooMuchAt = now
      return this.change(next, now)
    }
    this.behindSince = null
    this.settling = false
    if (!easy) {
      this.easySince = null
      return null
    }
    // 画面不动的时候也算"轻松"：主线程空着、解码器里没有帧。放开以后要是又跟不上，会再降下来
    this.easySince ??= now
    if (!this.cap || now - this.easySince < EASY_FOR_MS || now - this.changedAt < EASY_FOR_MS) return null
    this.easySince = now
    const service = this.serviceMs
    if ((service !== null && service < HEADROOM_SHARE * interval) || now - this.tooMuchAt > REMEMBER_MS) this.tooMuch = 0
    let next = this.cap + Math.max(1, Math.round(this.cap * STEP_UP_SHARE))
    if (this.tooMuch) next = Math.min(next, this.tooMuch - 1)
    if (next <= this.cap) return null
    // 量到过解一帧要多久的话，按新的帧率还解得过来（留两成余地）才放开
    if (service !== null && service > STEP_SLACK * (1000 / Math.min(next, advertised))) return null
    return this.change(next >= advertised ? 0 : next, now)
  }

  /** 从头来（换了线路、对面换了连接）。之前请对方限过帧率的话返回 0（要告诉它不限了），否则 null */
  reset(): number | null {
    const was = this.cap
    this.cap = 0
    this.outputs.length = 0
    this.probes.length = 0
    this.arrivals.clear()
    this.services.length = 0
    this.serviceMs = null
    this.behindSince = null
    this.easySince = null
    this.changedAt = Number.NEGATIVE_INFINITY
    this.settling = false
    this.tooMuch = 0
    this.tooMuchAt = Number.NEGATIVE_INFINITY
    return was ? 0 : null
  }

  private change(cap: number, now: number): number {
    this.cap = cap
    this.changedAt = now
    this.settling = true
    this.behindSince = null
    return cap
  }

  /** 丢掉一秒以前的记录（每记一笔都调，每次只丢几条） */
  private trim(now: number): void {
    while (this.outputs.length && (now - this.outputs[0]! > WINDOW_MS || this.outputs.length > MAX_RECORDS)) this.outputs.shift()
    while (this.probes.length && (now - this.probes[0]!.at > WINDOW_MS || this.probes.length > MAX_RECORDS)) this.probes.shift()
    while (this.services.length && (now - this.services[0]!.at > WINDOW_MS || this.services.length > MAX_RECORDS)) this.services.shift()
  }

  private forget(now: number): void {
    this.trim(now)
    for (const [ts, at] of this.arrivals) if (now - at > FORGET_MS) this.arrivals.delete(ts)
    // 画面不动时量不到：留着上一次量到的
    if (this.services.length) {
      const sorted = this.services.map((x) => x.ms).sort((a, b) => a - b)
      this.serviceMs = sorted[Math.floor(sorted.length / 2)]!
    }
  }
}
