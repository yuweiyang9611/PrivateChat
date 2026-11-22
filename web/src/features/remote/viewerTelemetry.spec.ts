import { describe, expect, it } from 'vitest'
import { describeBrowser, MAX_KEPT_SAMPLES, MAX_NOTE_LENGTH, MAX_NOTES, Notes, Series, ViewerTelemetry, type ViewerEnvironment, type ViewerState } from './viewerTelemetry'

const RELAY: ViewerState = { visible: true, connected: true, line: 'relay' }
const HIDDEN: ViewerState = { ...RELAY, visible: false }
const FAST: ViewerState = { ...RELAY, line: 'realtime' }
const ENV: ViewerEnvironment = { browser: 'Edge 142', platform: 'Windows', screenWidth: 1920, screenHeight: 1080, devicePixelRatio: 1.25, hardwareConcurrency: 8 }

/** 连上了、对方说它每秒发 30 帧（已经说过两次），还没出第一帧 */
function started(now = 0, state: ViewerState = RELAY): ViewerTelemetry {
  const t = new ViewerTelemetry()
  t.agentStats(30)
  t.agentStats(30)
  t.update(now, state)
  return t
}

interface Play { interval?: number; offset?: number; state?: ViewerState; sent?: number }

/**
 * 从 from 到 to（不含 to）：每 interval 毫秒画出一帧（这一帧在对方电脑上的时间 = 画出来的时刻 + offset），
 * 每到整秒对方的统计到一次（它说这一秒发了 sent 帧）、RemoteClient 的定时器 tick 一次（同一时刻先画帧再 tick）
 */
function play(t: ViewerTelemetry, from: number, to: number, o: Play = {}): void {
  const interval = o.interval ?? 1000 / 30
  let frameAt = from
  let tickAt = Math.ceil(from / 1000) * 1000
  while (frameAt < to || tickAt < to) {
    if (frameAt <= tickAt && frameAt < to) {
      t.frame(frameAt, frameAt + (o.offset ?? 0))
      frameAt += interval
    } else {
      t.agentStats(o.sent ?? 30)
      t.tick(tickAt, o.state ?? RELAY)
      tickAt += 1000
    }
  }
}

/** 从 from 到 to（不含 to）一帧也没画：只有每秒的统计和 tick */
function quiet(t: ViewerTelemetry, from: number, to: number, o: Play = {}): void {
  for (let at = Math.ceil(from / 1000) * 1000; at < to; at += 1000) {
    t.agentStats(o.sent ?? 30)
    t.tick(at, o.state ?? RELAY)
  }
}

describe('每秒的数：和被控端一样的统计', () => {
  it('百分位取排好序后的第 ceil(p×n) 个，平均、最小、最大按全部样本，保留一位小数', () => {
    const s = new Series()
    for (const v of [10, 1, 9, 2, 8, 3, 7, 4, 6, 5]) s.add(v)
    s.add(Number.NaN)
    expect(s.stat()).toEqual({ samples: 10, mean: 5.5, min: 1, p10: 1, p50: 5, p90: 9, max: 10 })
    expect(new Series().stat()).toEqual({ samples: 0, mean: 0, min: 0, p10: 0, p50: 0, p90: 0, max: 0 })
    const third = new Series()
    third.add(1 / 3)
    expect(third.stat().mean).toBe(0.3)
  })

  it('满 2 小时的样本就隔一个扔一个：留下的还铺满整段，个数和平均照全部算', () => {
    const s = new Series()
    const n = MAX_KEPT_SAMPLES * 2 + 10
    for (let i = 0; i < n; i++) s.add(i < n / 2 ? 10 : 20)
    const stat = s.stat()
    expect(stat.samples).toBe(n)
    expect(stat.mean).toBe(15)
    expect(stat.p10).toBe(10)
    expect(stat.p90).toBe(20)
    const kept = (s.toJSON() as { kept: number[] }).kept.length
    expect(kept).toBeLessThan(MAX_KEPT_SAMPLES)
    expect(kept).toBeGreaterThan(MAX_KEPT_SAMPLES / 4)
  })
})

describe('出过的问题：同样的只留一条', () => {
  it('同样的说明记次数；不同的太多，后来的合成最后一条；太长的截掉', () => {
    const notes = new Notes()
    for (let i = 0; i < 3; i++) notes.add('快速线路：连接断开')
    notes.add('  解码出错：\n EncodingError ')
    expect(notes.list()).toEqual(['快速线路：连接断开（3 次）', '解码出错： EncodingError'])

    const many = new Notes()
    for (let i = 0; i < 25; i++) many.add(`问题 ${i}`)
    const list = many.list()
    expect(list).toHaveLength(MAX_NOTES)
    expect(list.at(-1)).toBe('另有 6 次别的问题')

    const long = new Notes()
    long.add('长'.repeat(500))
    expect(Array.from(long.list()[0]!)).toHaveLength(MAX_NOTE_LENGTH)
    long.add('   ')
    expect(long.list()).toHaveLength(1)
  })
})

describe('卡住：该有画面却超过 500 ms 没出新帧', () => {
  it('一直 30 帧：不算卡住；解码帧率 30', () => {
    const t = started()
    play(t, 0, 10_000)
    const s = t.snapshot(10_000, ENV, false)
    expect(s.freezes).toBe(0)
    expect(s.decodedFps.samples).toBe(9) // 0 ms 那次 tick 之前没有一整秒；最后不满一秒的不算
    expect(s.decodedFps.p50).toBeGreaterThanOrEqual(29)
    expect(s.decodedFps.p50).toBeLessThanOrEqual(31)
  })

  it('帧晚到了（网络堵、中继上一下子来一批）：算一次，按这边实际停了多久', () => {
    const t = started()
    play(t, 0, 5000)
    // 对方 5000 ms 时发的那一帧 5800 ms 才画出来，之后的帧都晚 800 ms
    play(t, 5800, 8000, { offset: -800 })
    const s = t.snapshot(8000, ENV, false)
    expect(s.freezes).toBe(1)
    expect(s.longestFreezeMs).toBeGreaterThanOrEqual(790)
    expect(s.longestFreezeMs).toBeLessThan(850)
    expect(s.freezeMsTotal).toBe(s.longestFreezeMs)
  })

  it('对方屏幕不动（中继上一帧不发）：再动起来时隔了很久也不算', () => {
    const t = started()
    play(t, 0, 3000)
    quiet(t, 3000, 8000, { sent: 0 })
    play(t, 8000, 11_000)
    const s = t.snapshot(11_000, ENV, false)
    expect(s.freezes).toBe(0)
    // 屏幕不动的那几秒、刚动起来的那一秒没有解码帧率样本（0 帧、几帧是因为对方没发）
    expect(s.decodedFps.samples).toBe(4)
    expect(s.decodedFps.min).toBeGreaterThanOrEqual(29)
  })

  it('快速线路上屏幕不动、2 秒一帧保活：不算', () => {
    const t = started(0, { ...RELAY, line: 'realtime' })
    play(t, 0, 3000, { state: { ...RELAY, line: 'realtime' } })
    play(t, 3000, 12_000, { interval: 2000, sent: 1, state: { ...RELAY, line: 'realtime' } })
    expect(t.snapshot(12_000, ENV, false).freezes).toBe(0)
  })

  it('对方电脑慢、每秒只出一两帧：帧本来就隔这么久，不算卡住（解码帧率低另有统计）', () => {
    const t = started()
    play(t, 0, 10_000, { interval: 700, sent: 2 })
    const s = t.snapshot(10_000, ENV, false)
    expect(s.freezes).toBe(0)
    expect(s.decodedFps.p50).toBeLessThan(2)
  })

  it('丢了帧、这期间要过关键帧（中间的帧画不出来）：帧没晚到也算', () => {
    const t = started()
    play(t, 0, 3000)
    t.keyFrameRequest(3200)
    // 关键帧是对方 3700 ms 时编的，按时到了：晚到的判断管不着，靠"要过关键帧"
    play(t, 3700, 6000)
    const s = t.snapshot(6000, ENV, false)
    expect(s.freezes).toBe(1)
    expect(s.longestFreezeMs).toBeGreaterThan(700)
    expect(s.keyFrameRequests).toBe(1)
  })

  it('页面在后台的时间不算；回到前台从出第一帧重新算', () => {
    const t = started()
    play(t, 0, 3000)
    t.update(3100, HIDDEN)
    t.keyFrameRequest(5000)
    quiet(t, 3100, 10_000, { state: HIDDEN })
    t.update(10_000, RELAY)
    play(t, 10_050, 12_000)
    expect(t.snapshot(12_000, ENV, false).freezes).toBe(0)
  })

  it('画面停着、要过关键帧，接着页面进了后台：算到进后台为止', () => {
    const t = started()
    play(t, 0, 3000)
    t.keyFrameRequest(3100)
    t.update(3900, HIDDEN)
    const s = t.snapshot(5000, ENV, false)
    expect(s.freezes).toBe(1)
    expect(s.longestFreezeMs).toBeGreaterThan(900)
    expect(s.longestFreezeMs).toBeLessThan(950)
  })

  it('刚连上、还没出第一帧：不算（那是在等画面，不是卡住）', () => {
    const t = started()
    t.keyFrameRequest(100)
    play(t, 2000, 4000)
    expect(t.snapshot(4000, ENV, false).freezes).toBe(0)
  })

  it('选了快速线路、它还没接上（没有画面）：不看；换线路后两条线路的帧时间对不上，从头算', () => {
    const t = started()
    play(t, 0, 3000)
    t.update(3000, { ...RELAY, line: 'connecting' })
    t.keyFrameRequest(3500)
    t.update(6000, { ...RELAY, line: 'realtime' })
    // 快速线路上的帧时间从 0 开始
    play(t, 6200, 8000, { offset: -6200, state: { ...RELAY, line: 'realtime' } })
    expect(t.snapshot(8000, ENV, false).freezes).toBe(0)
  })

  it('中继上屏幕停了一分钟，刚动起来的第一帧大、晚到一点：不算；真晚到 800 ms 也只算这 800 ms，不算上那一分钟', () => {
    const t = started()
    play(t, 0, 3000)
    quiet(t, 3000, 63_000, { sent: 0 })
    // 对方 63000 ms 时编的第一帧 63400 ms 才画出来（大帧，比上一帧多花了 400 ms），之后的帧都晚 400 ms
    play(t, 63_400, 66_000, { offset: -400 })
    expect(t.snapshot(66_000, ENV, false).freezes).toBe(0)

    const late = started()
    play(late, 0, 3000)
    quiet(late, 3000, 63_000, { sent: 0 })
    play(late, 63_800, 66_000, { offset: -800 })
    const s = late.snapshot(66_000, ENV, false)
    expect(s.freezes).toBe(1)
    expect(s.longestFreezeMs).toBeGreaterThanOrEqual(800)
    expect(s.longestFreezeMs).toBeLessThan(850)
  })

  it('快速线路上屏幕不动（2 秒一帧保活），动起来的第一帧大、晚到一点：不算', () => {
    const t = started(0, FAST)
    play(t, 0, 3000, { state: FAST })
    play(t, 4000, 7000, { interval: 2000, sent: 1, state: FAST })
    // 用户 7500 ms 时动了一下：对方这时编的那一帧 7800 ms 才画出来，之后 30 帧、都晚 300 ms
    play(t, 7800, 10_000, { offset: -300, state: FAST })
    expect(t.snapshot(10_000, ENV, false).freezes).toBe(0)
  })

  it('快速线路上屏幕不动、丢了一个保活帧，等 1 秒才去要关键帧：最多算 1 秒多，不从上一个保活帧算', () => {
    const t = started(0, FAST)
    play(t, 0, 3000, { state: FAST })
    play(t, 4000, 7000, { interval: 2000, sent: 1, state: FAST })
    // 8000 ms 的保活帧丢了一个补不回来的包：9000 ms 去要关键帧，9150 ms 画出来（对方 9050 ms 编的）
    quiet(t, 7000, 9000, { sent: 1, state: FAST })
    t.keyFrameRequest(9000)
    // 还没画出来的时候报一份：也从 8000 ms 算
    const during = t.snapshot(9100, ENV, false)
    expect(during.freezes).toBe(1)
    expect(during.longestFreezeMs).toBe(1100)
    t.frame(9150, 9050)
    quiet(t, 9150, 11_000, { sent: 1, state: FAST })
    const s = t.snapshot(11_000, ENV, false)
    expect(s.freezes).toBe(1)
    expect(s.longestFreezeMs).toBe(1150)
  })

  it('中继上屏幕停了一分钟，动起来的第一帧解码出错、300 ms 后关键帧到了：从要关键帧之前 1 秒算', () => {
    const t = started()
    play(t, 0, 3000)
    quiet(t, 3000, 63_000, { sent: 0 })
    t.keyFrameRequest(63_000)
    t.frame(63_300, 63_250)
    play(t, 63_333, 66_000, { offset: -50 })
    const s = t.snapshot(66_000, ENV, false)
    expect(s.freezes).toBe(1)
    expect(s.longestFreezeMs).toBe(1300)
  })

  it('屏幕停了很久、要了关键帧还没到，页面进了后台：从要关键帧之前 1 秒算到进后台', () => {
    const t = started()
    play(t, 0, 3000)
    quiet(t, 3000, 30_000, { sent: 0 })
    t.keyFrameRequest(30_000)
    t.update(30_700, HIDDEN)
    const s = t.snapshot(31_000, ENV, false)
    expect(s.freezes).toBe(1)
    expect(s.longestFreezeMs).toBe(1700)
  })

  it('快速线路上一直 30 帧、丢了一帧（中间的帧画不出来，要了关键帧）：照样算，从上一帧算起', () => {
    const t = started(0, FAST)
    play(t, 0, 3000, { state: FAST })
    // 最后一帧在 2966.7 ms；下一帧丢了，3450 ms 去要关键帧，3600 ms 画出来（对方 3530 ms 编的）
    t.keyFrameRequest(3450)
    t.frame(3600, 3530)
    play(t, 3633, 6000, { offset: -70, state: FAST })
    const s = t.snapshot(6000, ENV, false)
    expect(s.freezes).toBe(1)
    expect(s.longestFreezeMs).toBeGreaterThanOrEqual(630)
    expect(s.longestFreezeMs).toBeLessThan(640)
  })

  it('还停着的时候报一份：正停着的也算进去，但不重复记', () => {
    const t = started()
    play(t, 0, 3000)
    t.keyFrameRequest(3100)
    const during = t.snapshot(4000, ENV, false)
    expect(during.freezes).toBe(1)
    expect(during.longestFreezeMs).toBeGreaterThan(1000)
    play(t, 4100, 5000)
    const after = t.snapshot(5000, ENV, false)
    expect(after.freezes).toBe(1)
    expect(after.longestFreezeMs).toBeGreaterThan(1100)
  })
})

describe('时长、线路、帧率上限、快速线路的问题', () => {
  it('总时长和各线路上的时长；关掉之后不算，接手回来接着算；定时器停了很久的那段不算', () => {
    const t = started()
    play(t, 0, 10_000)
    expect(t.snapshot(10_000, ENV, false)).toMatchObject({ seconds: 10, relaySeconds: 10, realtimeSeconds: 0 })
    t.stop(10_000)
    expect(t.snapshot(60_000, ENV, false).seconds).toBe(10)
    // 在这个窗口接手回来，换到快速线路
    const fast: ViewerState = { ...RELAY, line: 'realtime' }
    t.update(60_000, fast)
    play(t, 60_000, 65_000, { state: fast })
    // 电脑睡了一会儿：定时器停了 5 分钟（64 秒那次 tick 之后的都不算）
    t.tick(365_000, fast)
    play(t, 365_000, 367_000, { state: fast })
    expect(t.snapshot(367_000, ENV, false)).toMatchObject({ seconds: 16, relaySeconds: 10, realtimeSeconds: 6 })
  })

  it('没连着对方（等对方电脑连上来）的时间算总时长，不算在哪条线路上', () => {
    const t = new ViewerTelemetry()
    t.update(0, { ...RELAY, connected: false })
    quiet(t, 0, 5000, { state: { ...RELAY, connected: false } })
    expect(t.started).toBe(true)
    expect(t.snapshot(5000, ENV, false)).toMatchObject({ seconds: 5, relaySeconds: 0, realtimeSeconds: 0 })
    expect(new ViewerTelemetry().started).toBe(false)
  })

  it('请对方少发：往下压的次数和压到的最低（放开、往上调不算）', () => {
    const t = started()
    for (const cap of [25, 20, 24, 0, 28, 0]) t.maxFps(cap)
    expect(t.snapshot(0, ENV, false)).toMatchObject({ maxFpsCaps: 3, minMaxFps: 20 })
  })

  it('快速线路的问题和重新接的次数；中继断线；解码出错；没走快速线路时缓冲和丢包是 null', () => {
    const t = started()
    expect(t.snapshot(0, ENV, false)).toMatchObject({ jitterBufferMs: null, lossPermille: null, lineProblems: [], errors: [] })
    t.realtimeFailed('连接断开', true)
    t.realtimeFailed('连接断开', true)
    t.realtimeFailed('10 秒没有出画面', false)
    t.realtimeRetried()
    t.relayDropped()
    t.error('解码出错：EncodingError')
    t.realtimeReport({ jitterBufferMs: 12, lossPermille: 3 })
    t.realtimeReport({ jitterBufferMs: 20, lossPermille: 0 })
    const s = t.snapshot(0, ENV, true)
    expect(s.realtimeReconnects).toBe(3)
    expect(s.lineProblems).toEqual(['快速线路：连接断开（2 次）', '快速线路：10 秒没有出画面', '标准线路：连接断开，重新连接'])
    expect(s.errors).toEqual(['解码出错：EncodingError'])
    expect(s.jitterBufferMs).toMatchObject({ samples: 2, p50: 12, max: 20 })
    expect(s.lossPermille).toMatchObject({ samples: 2, max: 3 })
    expect(s).toMatchObject({ final: true, browser: 'Edge 142', platform: 'Windows', screenWidth: 1920, screenHeight: 1080, devicePixelRatio: 1.25, hardwareConcurrency: 8 })
  })

  it('每记一笔 revision 加一（中途那份没变就不用报）', () => {
    const t = started()
    const r0 = t.revision
    t.update(100, RELAY)
    t.agentStats(30)
    expect(t.revision).toBe(r0)
    t.keyFrameRequest(200)
    expect(t.revision).toBe(r0 + 1)
  })
})

describe('刷新页面接着记', () => {
  it('存下来再读回来：汇总一样', () => {
    const t = started()
    play(t, 0, 5000)
    play(t, 5800, 8000, { offset: -800 })
    t.realtimeFailed('连接断开', true)
    t.error('解码出错：EncodingError')
    t.maxFps(20)
    t.realtimeReport({ jitterBufferMs: 15, lossPermille: 2 })
    t.stop(8000)
    const restored = ViewerTelemetry.restore(JSON.parse(JSON.stringify(t)))
    expect(restored).not.toBeNull()
    expect(restored!.snapshot(9000, ENV, true)).toEqual(t.snapshot(9000, ENV, true))
    // 读回来接着记
    restored!.update(9000, RELAY)
    play(restored!, 9000, 12_000)
    expect(restored!.snapshot(12_000, ENV, true).seconds).toBe(11)
  })

  it('不认识的东西不读', () => {
    expect(ViewerTelemetry.restore(null)).toBeNull()
    expect(ViewerTelemetry.restore({ v: 2 })).toBeNull()
    expect(ViewerTelemetry.restore('x')).toBeNull()
    const junk = ViewerTelemetry.restore({ v: 1, freezes: 'many', decodedFps: { kept: ['a', 3], count: -1 }, errors: { items: [{ text: 5 }] } })
    expect(junk).not.toBeNull()
    expect(junk!.snapshot(0, ENV, false)).toMatchObject({ freezes: 0, errors: [], decodedFps: { samples: 1, p50: 3 } })
  })
})

describe('浏览器：只报品牌和大版本、系统', () => {
  const chromeUa = (extra = '') => `Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36${extra}`

  it('Chromium 系看 userAgentData：Edge、手机上的 Chrome；GREASE 品牌不算', () => {
    expect(describeBrowser({
      userAgent: chromeUa(' Edg/142.0.0.0'),
      userAgentData: { brands: [{ brand: 'Chromium', version: '142' }, { brand: 'Microsoft Edge', version: '142' }, { brand: 'Not_A Brand', version: '99' }], platform: 'Windows', mobile: false },
    })).toEqual({ browser: 'Edge 142', platform: 'Windows' })
    expect(describeBrowser({
      userAgent: 'Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36',
      userAgentData: { brands: [{ brand: 'Not)A;Brand', version: '8' }, { brand: 'Chromium', version: '141' }, { brand: 'Google Chrome', version: '141' }], platform: 'Android', mobile: true },
    })).toEqual({ browser: 'Chrome 141', platform: 'Android（手机）' })
  })

  it('没有 userAgentData：从 User-Agent 里认（Safari、Firefox、iPad 冒充 Mac）', () => {
    expect(describeBrowser({ userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1' }))
      .toEqual({ browser: 'Safari 18', platform: 'iOS' })
    expect(describeBrowser({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15', maxTouchPoints: 5 }))
      .toEqual({ browser: 'Safari 18', platform: 'iPadOS' })
    expect(describeBrowser({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15', maxTouchPoints: 0 }))
      .toEqual({ browser: 'Safari 18', platform: 'macOS' })
    expect(describeBrowser({ userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0' }))
      .toEqual({ browser: 'Firefox 133', platform: 'Windows' })
    expect(describeBrowser({ userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) EdgiOS/131.0.2903.92 Version/18.0 Mobile/15E148 Safari/604.1' }).browser)
      .toBe('Edge 131')
    expect(describeBrowser({ userAgent: 'something odd' })).toEqual({ browser: '未知', platform: '未知' })
  })

  it('无界面的浏览器（端到端测试里）也认得出来', () => {
    expect(describeBrowser({ userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/142.0.0.0 Safari/537.36 Edg/142.0.0.0' }))
      .toEqual({ browser: 'Edge 142', platform: 'Windows' })
    expect(describeBrowser({ userAgent: chromeUa(), userAgentData: { brands: [{ brand: 'HeadlessEdge', version: '142' }, { brand: 'Chromium', version: '142' }], platform: 'Windows' } }).browser)
      .toBe('Edge 142')
  })

  it('不认识的 Chromium 系（没报自己的品牌）：按 User-Agent 里的 Chrome 报', () => {
    expect(describeBrowser({ userAgent: chromeUa(), userAgentData: { brands: [{ brand: 'Chromium', version: '140' }, { brand: 'Not.A/Brand', version: '24' }], platform: 'Linux' } }))
      .toEqual({ browser: 'Chrome 142', platform: 'Linux' })
  })
})
