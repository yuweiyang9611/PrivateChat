import { describe, expect, it } from 'vitest'
import {
  codecFromAnnexB, CUSTOM_DEFAULT, CUSTOM_FPS, CUSTOM_KBPS, CUSTOM_RESOLUTIONS, encode, Features, FrameType, legacyQualityOf, MAX_TEXT_BYTES, modifiersOf, normalizePoint,
  normalizeVideoSettings, parseAgentFrame, Quality, scanCodeOf, SpecialKey, StatsFlags, Transport, TransportState, utf8Length, VIDEO_MODE_KEYS, VIDEO_PRESETS, VideoMode, WheelAccumulator,
} from './protocol'

const buf = (bytes: number[]) => new Uint8Array(bytes).buffer

describe('Agent 帧解析（与 C# RemoteFrames 的布局一致）', () => {
  it('视频配置、视频帧、显示器、统计、状态', () => {
    // 0.7.0 起多一个字节：支持哪些可选功能
    expect(parseAgentFrame(buf([0x01, 1, 0x80, 0x07, 0x38, 0x04, 20, 1, 3]))).toEqual({ type: 'config', codec: 1, width: 1920, height: 1080, fps: 20, monitor: 1, features: Features.Audio | Features.AdaptiveBitrate })
    const video = parseAgentFrame(buf([0x02, 4, 3, 2, 1, 1, 9, 9]))
    expect(video).toMatchObject({ type: 'video', timestamp: 0x01020304, key: true })
    expect(Array.from((video as { data: Uint8Array }).data)).toEqual([9, 9])
    expect(parseAgentFrame(buf([0x04, 1, 0, 0x00, 0x05, 0xd0, 0x02, 1]))).toEqual({ type: 'monitors', monitors: [{ index: 0, width: 1280, height: 720, primary: true }] })
    expect(parseAgentFrame(buf([0x06, 20, 0xe8, 0x03, 100, 85, 0, 0x58, 0x02, 0xc4, 0x09]))).toEqual({ type: 'stats', fps: 20, kbps: 1000, cpu: 100, rtt: 85, target: 600, max: 2500, captureMs: 0, encodeMs: 0, flags: 0 })
    expect(parseAgentFrame(buf([0x07, 1, 0x78]))).toEqual({ type: 'status', code: 1, message: 'x' })
    expect(parseAgentFrame(new Uint8Array([0x05, ...new TextEncoder().encode('你好')]).buffer)).toEqual({ type: 'clipboard', text: '你好' })
  })

  it('声音包、探测包', () => {
    const audio = parseAgentFrame(buf([0x08, 4, 3, 2, 1, 1, 2, 7, 7, 7]))
    expect(audio).toMatchObject({ type: 'audio', timestamp: 0x01020304, codec: 1, channels: 2 })
    expect(Array.from((audio as { data: Uint8Array }).data)).toEqual([7, 7, 7])
    expect(parseAgentFrame(buf([0x09, 0x2a, 0, 0, 0]))).toEqual({ type: 'ping', sequence: 42 })
    expect(parseAgentFrame(buf([0x08, 0, 0, 0, 0, 1, 2]))).toBeNull() // 没有数据
    expect(parseAgentFrame(buf([0x09, 1, 2]))).toBeNull()
  })

  it('线路状态：状态、第几次发布、说明', () => {
    expect(parseAgentFrame(buf([0x0a, 1, 3]))).toEqual({ type: 'transport', state: TransportState.Realtime, generation: 3, detail: '' })
    expect(parseAgentFrame(new Uint8Array([0x0a, 2, 0, ...new TextEncoder().encode('连不上')]).buffer)).toEqual({ type: 'transport', state: TransportState.RealtimeFailed, generation: 0, detail: '连不上' })
    expect(parseAgentFrame(buf([0x0a, 1]))).toBeNull()
  })

  it('老版本被控端（0.6 及以前）发来的帧照样能解：没有的字段按 0 算', () => {
    expect(parseAgentFrame(buf([0x01, 1, 0x80, 0x07, 0x38, 0x04, 20, 1]))).toEqual({ type: 'config', codec: 1, width: 1920, height: 1080, fps: 20, monitor: 1, features: 0 })
    expect(parseAgentFrame(buf([0x06, 20, 0xe8, 0x03, 100, 85, 0]))).toEqual({ type: 'stats', fps: 20, kbps: 1000, cpu: 100, rtt: 85, target: 0, max: 0, captureMs: 0, encodeMs: 0, flags: 0 })
  })

  it('0.8.6 的统计帧 16 字节：每帧采集 / 编码耗时（0.1 ms 为单位）和标志位', () => {
    // 采集 12.5 ms（125 = 0x7d）、编码 45.3 ms（453 = 0x01c5）；自动画质因为电脑忙不过来降了分辨率，这一阵也跟不上
    const stats = parseAgentFrame(buf([0x06, 21, 0x40, 0x1f, 30, 9, 0, 0xa0, 0x0f, 0x40, 0x1f, 0x7d, 0x00, 0xc5, 0x01, StatsFlags.LoweredForCpu | StatsFlags.CpuBound]))
    expect(stats).toEqual({ type: 'stats', fps: 21, kbps: 8000, cpu: 30, rtt: 9, target: 4000, max: 8000, captureMs: 12.5, encodeMs: 45.3, flags: 6 })
    // 老版本（0.7 ~ 0.8.5）的 11 字节：新加的字段按 0 算
    expect(parseAgentFrame(buf([0x06, 21, 0x40, 0x1f, 30, 9, 0, 0xa0, 0x0f, 0x40, 0x1f]))).toMatchObject({ target: 4000, max: 8000, captureMs: 0, encodeMs: 0, flags: 0 })
    // 再往后加了字段的新版本：多出来的不管
    expect(parseAgentFrame(buf([0x06, 21, 0x40, 0x1f, 30, 9, 0, 0xa0, 0x0f, 0x40, 0x1f, 0x7d, 0x00, 0xc5, 0x01, 1, 0xff, 0xff]))).toMatchObject({ captureMs: 12.5, encodeMs: 45.3, flags: StatsFlags.LoweredForNetwork })
  })

  it('截断的、方向不对的帧不解析', () => {
    expect(parseAgentFrame(buf([]))).toBeNull()
    expect(parseAgentFrame(buf([0x01, 1, 0x80]))).toBeNull()
    expect(parseAgentFrame(buf([0x04, 2, 0, 0, 5, 0xd0, 2, 1]))).toBeNull()
    expect(parseAgentFrame(buf([FrameType.MouseMove, 0, 0, 0, 0, 0, 0, 0, 0]))).toBeNull()
  })
})

describe('控制端帧编码', () => {
  it('键鼠帧的字节与协议一致', () => {
    const move = encode.mouseMove(0.25, 1)
    const v = new DataView(move.buffer)
    expect([move[0], v.getFloat32(1, true), v.getFloat32(5, true)]).toEqual([0x10, 0.25, 1])
    expect(Array.from(encode.mouseButton(2, true))).toEqual([0x11, 2, 1])
    expect(Array.from(encode.wheel(-120, 99999))).toEqual([0x12, 0x88, 0xff, 0xff, 0x7f])
    expect(Array.from(encode.key(0xe04b, true, 3))).toEqual([0x13, 0x4b, 0xe0, 1, 3])
    expect(Array.from(encode.frameAck(0x01020304))).toEqual([0x19, 4, 3, 2, 1])
    expect(Array.from(encode.specialKey(SpecialKey.TaskManager))).toEqual([0x17, 4])
    expect(Array.from(encode.requestKeyFrame())).toEqual([0x16])
    expect(Array.from(encode.setAudio(true))).toEqual([0x1b, 1])
    expect(Array.from(encode.setAudio(false))).toEqual([0x1b, 0])
    expect(Array.from(encode.pong(0x01020304))).toEqual([0x1c, 4, 3, 2, 1])
    expect(new TextDecoder().decode(encode.text('中文').subarray(1))).toBe('中文')
  })

  it('换线路、报接收情况（与 C# 的字节一致；超出范围的数夹住）', () => {
    expect(Array.from(encode.setTransport(Transport.Realtime))).toEqual([0x1d, 1])
    expect(Array.from(encode.setTransport(Transport.Relay))).toEqual([0x1d, 0])
    expect(Array.from(encode.receiverReport({ lossPermille: 25, receivedKbps: 1800, jitterBufferMs: 40, fps: 17, rttMs: 12 }))).toEqual([0x1e, 25, 0, 0x08, 0x07, 40, 0, 17, 12, 0])
    expect(Array.from(encode.receiverReport({ lossPermille: 5000, receivedKbps: 99999, jitterBufferMs: -3, fps: 999, rttMs: NaN }))).toEqual([0x1e, 0xe8, 0x03, 0xff, 0xff, 0, 0, 255, 0, 0])
    expect(Array.from(encode.maxFps(21))).toEqual([0x1f, 21])
    expect(Array.from(encode.maxFps(0))).toEqual([0x1f, 0])
    expect(Array.from(encode.maxFps(999))).toEqual([0x1f, 255])
    expect(Array.from(encode.maxFps(-2))).toEqual([0x1f, 0])
  })

  it('画质（SetVideo）：9 字节，模式、最大宽高、帧率、码率上限，小端', () => {
    expect(Array.from(encode.setVideo(VIDEO_PRESETS.auto))).toEqual([0x20, 0, 0x80, 0x07, 0xb0, 0x04, 30, 0x40, 0x1f])
    expect(Array.from(encode.setVideo(VIDEO_PRESETS.smooth))).toEqual([0x20, 1, 0x00, 0x05, 0x20, 0x03, 30, 0xa0, 0x0f])
    expect(Array.from(encode.setVideo(VIDEO_PRESETS.clear))).toEqual([0x20, 2, 0x80, 0x07, 0xb0, 0x04, 30, 0x40, 0x1f])
    // 原画：0×0 = 原始分辨率
    expect(Array.from(encode.setVideo(VIDEO_PRESETS.original))).toEqual([0x20, 3, 0, 0, 0, 0, 30, 0x80, 0x3e])
    expect(Array.from(encode.setVideo({ mode: VideoMode.Custom, maxWidth: 960, maxHeight: 600, fps: 15, maxKbps: 2000 }))).toEqual([0x20, 4, 0xc0, 0x03, 0x58, 0x02, 15, 0xd0, 0x07])
    // 超出范围的数夹住
    expect(Array.from(encode.setVideo({ mode: VideoMode.Custom, maxWidth: 99999, maxHeight: -1, fps: 999, maxKbps: NaN }))).toEqual([0x20, 4, 0xff, 0xff, 0, 0, 255, 0, 0])
    expect(FrameType.SetVideo).toBe(0x20)
    expect(Features.VideoSettings).toBe(8)
    expect(VIDEO_MODE_KEYS[VideoMode.Original]).toBe('original')
  })

  it('老被控端只认识两档：自动、流畅 → 流畅；清晰、原画 → 清晰；自定义看分辨率', () => {
    expect(legacyQualityOf(VIDEO_PRESETS.auto)).toBe(Quality.Smooth)
    expect(legacyQualityOf(VIDEO_PRESETS.smooth)).toBe(Quality.Smooth)
    expect(legacyQualityOf(VIDEO_PRESETS.clear)).toBe(Quality.Sharp)
    expect(legacyQualityOf(VIDEO_PRESETS.original)).toBe(Quality.Sharp)
    const custom = (maxWidth: number, maxHeight: number) => ({ mode: VideoMode.Custom, maxWidth, maxHeight, fps: 30, maxKbps: 8000 })
    expect(legacyQualityOf(custom(0, 0))).toBe(Quality.Sharp)
    expect(legacyQualityOf(custom(1600, 1000))).toBe(Quality.Sharp)
    expect(legacyQualityOf(custom(1280, 800))).toBe(Quality.Smooth)
    expect(legacyQualityOf(custom(960, 600))).toBe(Quality.Smooth)
  })

  it('这台设备上记着的画质：坏的、旧的、不认识的都有个合理的结果', () => {
    const auto = { ...VIDEO_PRESETS.auto }
    const def = { ...CUSTOM_DEFAULT }
    // 没记过、存坏了
    for (const raw of [null, undefined, 42, 'auto', [], {}, { current: null }, { current: 'x' }, { current: { mode: 9 } }, { current: { mode: -1 } }, { current: { mode: 1.5 } }, { current: { mode: '1' } }])
      expect(normalizeVideoSettings(raw)).toEqual({ current: auto, custom: def })
    // 固定档只认模式编号，数按现在的表来（老版本记下的数不作数）
    expect(normalizeVideoSettings({ current: { mode: VideoMode.Smooth, maxWidth: 1, maxHeight: 1, fps: 60, maxKbps: 1 } })).toEqual({ current: { ...VIDEO_PRESETS.smooth }, custom: def })
    expect(normalizeVideoSettings({ current: { mode: VideoMode.Original } }).current).toEqual(VIDEO_PRESETS.original)
    // 自定义：值都在列表里才认
    const mine = { mode: VideoMode.Custom, maxWidth: 1280, maxHeight: 800, fps: 15, maxKbps: 2000 }
    expect(normalizeVideoSettings({ current: mine, custom: mine })).toEqual({ current: mine, custom: mine })
    expect(normalizeVideoSettings({ current: { ...mine, maxWidth: 0, maxHeight: 0 } }).current).toEqual({ ...mine, maxWidth: 0, maxHeight: 0 })
    // 只记了现在用的自定义（e2e 脚本就是这么写的）：自定义里显示的也是它
    expect(normalizeVideoSettings({ current: mine })).toEqual({ current: mine, custom: mine })
    // 用的是固定档，自定义里选过的照样记着
    expect(normalizeVideoSettings({ current: { mode: VideoMode.Clear }, custom: mine })).toEqual({ current: { ...VIDEO_PRESETS.clear }, custom: mine })
    // 不在列表里的值（60 帧、不在表里的分辨率、宽高只填了一个、超出的码率、字符串）：现在用的当自动，自定义用默认的
    for (const bad of [{ fps: 60 }, { maxWidth: 1366, maxHeight: 768 }, { maxWidth: 0, maxHeight: 800 }, { maxKbps: 50000 }, { fps: '15' }, { maxKbps: undefined }])
      expect(normalizeVideoSettings({ current: { ...mine, ...bad }, custom: { ...mine, ...bad } })).toEqual({ current: auto, custom: def })
    // 自定义里的模式号记错了也不要紧：取出来一律是自定义
    expect(normalizeVideoSettings({ current: auto, custom: { ...mine, mode: VideoMode.Smooth } }).custom).toEqual(mine)
    // 默认值本身在列表里
    expect(CUSTOM_RESOLUTIONS.some(([w, h]) => w === CUSTOM_DEFAULT.maxWidth && h === CUSTOM_DEFAULT.maxHeight) && CUSTOM_FPS.includes(CUSTOM_DEFAULT.fps) && CUSTOM_KBPS.includes(CUSTOM_DEFAULT.maxKbps)).toBe(true)
    // 拿到的是副本：改了不会改到表里的
    const got = normalizeVideoSettings(null)
    got.current.fps = 5
    got.custom.fps = 5
    expect(VIDEO_PRESETS.auto.fps).toBe(30)
    expect(CUSTOM_DEFAULT.fps).toBe(30)
  })

  it('超过 1 MB 的文字截断在字符边界上', () => {
    const huge = encode.clipboard('好'.repeat(MAX_TEXT_BYTES))
    expect(huge.length - 1).toBeLessThanOrEqual(MAX_TEXT_BYTES)
    expect((huge.length - 1) % 3).toBe(0)
    expect(new TextDecoder('utf-8', { fatal: true }).decode(huge.subarray(1)).length).toBe((huge.length - 1) / 3)
    expect(utf8Length('好a')).toBe(4)
  })

  it('物理键位映射到扫描码，扩展键带 0xE0', () => {
    expect(scanCodeOf('KeyA')).toBe(0x1e)
    expect(scanCodeOf('Enter')).toBe(0x1c)
    expect(scanCodeOf('NumpadEnter')).toBe(0xe01c)
    expect(scanCodeOf('ArrowLeft')).toBe(0xe04b)
    expect(scanCodeOf('MetaLeft')).toBe(0xe05b)
    expect(scanCodeOf('Lang1')).toBeUndefined()
    // SendInput 按扫描码注入时 NumLock 是不带扩展标志的 0x45；Pause 发不了
    expect(scanCodeOf('NumLock')).toBe(0x45)
    expect(scanCodeOf('Pause')).toBeUndefined()
    expect(modifiersOf({ shiftKey: true, ctrlKey: false, altKey: true, metaKey: false })).toBe(5)
  })
})

describe('坐标与滚轮', () => {
  it('object-fit: contain 的黑边不算画面', () => {
    // 1000×500 的框里显示 16:9 的画面：高度占满 500，宽 888.9，左右各 55.6 的黑边
    const box = { left: 0, top: 0, width: 1000, height: 500 }
    const mid = normalizePoint(500, 250, box, 1920, 1080)!
    expect(mid.x).toBeCloseTo(0.5, 6)
    expect(mid.y).toBeCloseTo(0.5, 6)
    expect(normalizePoint(10, 250, box, 1920, 1080)).toBeNull()
    const p = normalizePoint(55.5556 + 888.889, 500, box, 1920, 1080)!
    expect(p.x).toBeCloseTo(1, 3)
    expect(p.y).toBeCloseTo(1, 3)
    expect(normalizePoint(1, 1, { left: 0, top: 0, width: 0, height: 0 }, 1920, 1080)).toBeNull()
  })

  it('滚轮按 120 一格换算，向上为正，零碎的累积', () => {
    const w = new WheelAccumulator()
    expect(w.push({ deltaX: 0, deltaY: 100, deltaMode: 0 })).toEqual({ dx: 0, dy: -120 })
    expect(w.push({ deltaX: 0, deltaY: -3, deltaMode: 1 })).toEqual({ dx: 0, dy: 120 })
    expect(w.push({ deltaX: 0, deltaY: 0.4, deltaMode: 0 })).toBeNull()
    expect(w.push({ deltaX: 0.5, deltaY: 0.5, deltaMode: 0 })).toEqual({ dx: 0, dy: -1 })
  })

  it('从关键帧的 SPS 取 codec 字符串', () => {
    const key = new Uint8Array([0, 0, 0, 1, 0x09, 0xf0, 0, 0, 0, 1, 0x67, 0x4d, 0x40, 0x1f, 0xaa, 0, 0, 1, 0x65, 0x88])
    expect(codecFromAnnexB(key)).toBe('avc1.4D401F')
    expect(codecFromAnnexB(new Uint8Array([0, 0, 1, 0x41, 0x9a]))).toBeNull()
  })
})
