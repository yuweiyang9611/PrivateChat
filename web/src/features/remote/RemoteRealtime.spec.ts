import { describe, expect, it } from 'vitest'
import { StallWatch, StuckWatch, withStereo } from './RemoteRealtime'

describe('快速线路：很久没有新画面才算断', () => {
  /** 从 from 到 to 每秒查一次，返回第一次判定"断了"的时刻（没有就是 null） */
  const checks = (watch: StallWatch, from: number, to: number, visible: boolean): number | null => {
    for (let now = from; now <= to; now += 1000) if (watch.stalled(now, visible)) return now
    return null
  }

  it('在前台：有帧就不算；7 秒没有新的一帧算断', () => {
    const watch = new StallWatch()
    for (let now = 0; now <= 20_000; now += 1000) {
      watch.frame(now) // 屏幕不动时被控端也会每 2 秒发一帧保活
      expect(watch.stalled(now, true)).toBe(false)
    }
    // 20 秒之后再没有帧
    expect(checks(watch, 21_000, 40_000, true)).toBe(28_000)
  })

  it('页面在后台的时间不算（浏览器这时不回调"出了一帧"），回到前台重新数', () => {
    const watch = new StallWatch()
    watch.frame(0)
    expect(watch.stalled(1000, true)).toBe(false)
    // 切到别的标签页 5 分钟：一帧回调都没有
    expect(checks(watch, 2000, 300_000, false)).toBeNull()
    // 回到前台：马上有帧就没事
    watch.frame(300_500)
    expect(checks(watch, 301_000, 305_000, true)).toBeNull()
    // 回到前台之后真的一直没有帧：从回来那一刻起数 7 秒
    const dead = new StallWatch()
    dead.frame(0)
    expect(checks(dead, 1000, 300_000, false)).toBeNull()
    expect(checks(dead, 301_000, 320_000, true)).toBe(308_000)
  })

  it('定时器停过一阵（页面被冻结、电脑睡过）不算', () => {
    const watch = new StallWatch()
    watch.frame(0)
    expect(watch.stalled(1000, true)).toBe(false)
    // 一小时后才又查到：这一小时不是"没有画面"，是页面根本没在跑
    expect(watch.stalled(3_600_000, true)).toBe(false)
    expect(checks(watch, 3_601_000, 3_620_000, true)).toBe(3_608_000)
  })
})

describe('快速线路：answer 里声明能收双声道', () => {
  const sdp = (opus: string): string =>
    ['v=0', 'm=audio 9 UDP/TLS/RTP/SAVPF 111 63', 'a=rtpmap:111 opus/48000/2', opus, 'a=rtpmap:63 red/48000/2', 'a=fmtp:63 111/111',
      'm=video 9 UDP/TLS/RTP/SAVPF 125', 'a=rtpmap:125 H264/90000', 'a=fmtp:125 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f', ''].join('\r\n')

  it('给 Opus 的参数加上 stereo=1，别的行不动', () => {
    const out = withStereo(sdp('a=fmtp:111 minptime=10;useinbandfec=1'))
    expect(out).toContain('a=fmtp:111 minptime=10;useinbandfec=1;stereo=1\r\n')
    expect(out).toContain('a=fmtp:63 111/111\r\n')
    expect(out).toContain('a=fmtp:125 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f\r\n')
    expect(out.split('\r\n').length).toBe(sdp('x').split('\r\n').length)
  })

  it('已经有 stereo 的不再加', () => {
    const already = sdp('a=fmtp:111 minptime=10;stereo=1;useinbandfec=1')
    expect(withStereo(already)).toBe(already)
    const off = sdp('a=fmtp:111 minptime=10;useinbandfec=1;stereo=0')
    expect(withStereo(off)).toBe(off)
  })

  it('没有 Opus 参数行时原样返回', () => {
    expect(withStereo('v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 125\r\n')).toBe('v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 125\r\n')
    expect(withStereo('')).toBe('')
  })
})

describe('快速线路：卡在补不回来的包上就自己要关键帧', () => {
  /**
   * 每 200 毫秒取一次统计，一步一步喂进去；step 说这一步里解出了几帧、要了几次重发、补到了几个包。
   * 返回每次判定"该要关键帧了"的时刻。
   */
  const run = (steps: { decoded?: number; nacks?: number; recovered?: number }[], rttMs = 10, watch = new StuckWatch()): number[] => {
    const asked: number[] = []
    let framesDecoded = 100
    let nackCount = 0
    let retransmitted = 0
    watch.sample(0, { framesDecoded, nackCount, retransmitted, rttMs })
    steps.forEach((step, i) => {
      framesDecoded += step.decoded ?? 0
      nackCount += step.nacks ?? 0
      retransmitted += step.recovered ?? 0
      const now = (i + 1) * 200
      if (watch.sample(now, { framesDecoded, nackCount, retransmitted, rttMs })) asked.push(now)
    })
    return asked
  }
  const repeat = <T>(n: number, step: T): T[] => Array.from({ length: n }, () => step)

  it('画面一直在出：不要', () => {
    expect(run(repeat(50, { decoded: 4 }))).toEqual([])
  })

  it('屏幕不动（没有新的一帧，也没人要重发）：不要', () => {
    expect(run(repeat(50, {}))).toEqual([])
    // 两秒一帧的保活
    expect(run(repeat(5, [...repeat(9, {}), { decoded: 1 }]).flat())).toEqual([])
  })

  it('丢了包、要一次就补到：不要', () => {
    expect(run([{ decoded: 4 }, { decoded: 1, nacks: 1 }, { decoded: 3, recovered: 1 }, ...repeat(20, { decoded: 4 })])).toEqual([])
    // 补到了，但屏幕正好不动、后面没有新的一帧
    expect(run([{ nacks: 1 }, { recovered: 1 }, ...repeat(20, {})])).toEqual([])
  })

  it('一直在要重发、补不到、也解不出新的一帧：0.4 秒多就要，不等浏览器的 3 秒', () => {
    // 每 200 毫秒里要两次（真实情况约 0.1 秒一次）
    const asked = run([{ decoded: 4 }, ...repeat(10, { nacks: 2 })])
    expect(asked[0]).toBe(800) // 第一次看到在要是 400，过了 400 毫秒
  })

  it('要到了关键帧就恢复，不再要', () => {
    expect(run([...repeat(4, { nacks: 2 }), { decoded: 1 }, ...repeat(20, { decoded: 4 })])).toEqual([600])
  })

  it('要了关键帧还是解不出来（关键帧也丢了包）：隔一秒半再要，不连着要', () => {
    const asked = run(repeat(25, { nacks: 2 }))
    expect(asked).toEqual([600, 2200, 3800])
  })

  it('只要过一两次重发：补的包可能还在路上，先不要', () => {
    expect(run([{ nacks: 1 }, {}, { nacks: 1 }, {}, {}, { decoded: 1 }])).toEqual([])
  })

  it('一大帧还在路上，丢的包一要就补到：不要', () => {
    // 慢的网络上一帧要传零点几秒，中间丢的包都补到了，这一秒里没有解出新的一帧
    expect(run([...repeat(4, { nacks: 1, recovered: 1 }), { decoded: 1 }, ...repeat(4, { nacks: 1, recovered: 1 }), { decoded: 1 }])).toEqual([])
  })

  it('丢包多的网络：别的包一直在补到，唯独卡住的那个补不回来，一秒没有新的一帧也要', () => {
    const asked = run(repeat(10, { nacks: 2, recovered: 1 }))
    expect(asked[0]).toBe(1200)
  })

  it('到 SFU 的往返很慢时多等一会儿（3 个往返）：补的包本来就回来得慢', () => {
    expect(run(repeat(6, { nacks: 2 }), 300)[0]).toBe(1200) // 200 起算，等 900 毫秒
    // 慢网络上要了三次、第四个 200 毫秒里补到了
    expect(run([{ nacks: 1 }, { nacks: 1 }, { nacks: 1 }, { recovered: 1, decoded: 1 }, ...repeat(5, { decoded: 2 })], 300)).toEqual([])
  })
})
