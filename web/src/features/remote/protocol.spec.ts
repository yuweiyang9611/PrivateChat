import { describe, expect, it } from 'vitest'
import { codecFromAnnexB, encode, FrameType, MAX_TEXT_BYTES, modifiersOf, normalizePoint, parseAgentFrame, scanCodeOf, SpecialKey, utf8Length, WheelAccumulator } from './protocol'

const buf = (bytes: number[]) => new Uint8Array(bytes).buffer

describe('Agent 帧解析（与 C# RemoteFrames 的布局一致）', () => {
  it('视频配置、视频帧、显示器、统计、状态', () => {
    expect(parseAgentFrame(buf([0x01, 1, 0x80, 0x07, 0x38, 0x04, 20, 1]))).toEqual({ type: 'config', codec: 1, width: 1920, height: 1080, fps: 20, monitor: 1 })
    const video = parseAgentFrame(buf([0x02, 4, 3, 2, 1, 1, 9, 9]))
    expect(video).toMatchObject({ type: 'video', timestamp: 0x01020304, key: true })
    expect(Array.from((video as { data: Uint8Array }).data)).toEqual([9, 9])
    expect(parseAgentFrame(buf([0x04, 1, 0, 0x00, 0x05, 0xd0, 0x02, 1]))).toEqual({ type: 'monitors', monitors: [{ index: 0, width: 1280, height: 720, primary: true }] })
    expect(parseAgentFrame(buf([0x06, 20, 0xe8, 0x03, 100, 85, 0]))).toEqual({ type: 'stats', fps: 20, kbps: 1000, cpu: 100, rtt: 85 })
    expect(parseAgentFrame(buf([0x07, 1, 0x78]))).toEqual({ type: 'status', code: 1, message: 'x' })
    expect(parseAgentFrame(new Uint8Array([0x05, ...new TextEncoder().encode('你好')]).buffer)).toEqual({ type: 'clipboard', text: '你好' })
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
    expect(new TextDecoder().decode(encode.text('中文').subarray(1))).toBe('中文')
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
