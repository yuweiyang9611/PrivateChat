import { describe, expect, it } from 'vitest'
import { StallWatch, withStereo } from './RemoteRealtime'

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
