/**
 * 远程控制数据面帧格式（docs/DESIGN.md 附录 C；C# 那份在 src/PrivateChat.Contracts/RemoteControl.cs）。
 * WebSocket 二进制消息，一条一帧：第 1 字节类型，其余载荷，小端。
 */

export const FrameType = {
  VideoConfig: 0x01,
  VideoFrame: 0x02,
  Monitors: 0x04,
  Clipboard: 0x05,
  Stats: 0x06,
  Status: 0x07,
  MouseMove: 0x10,
  MouseButton: 0x11,
  Wheel: 0x12,
  Key: 0x13,
  SelectMonitor: 0x14,
  SetQuality: 0x15,
  RequestKeyFrame: 0x16,
  SpecialKey: 0x17,
  Text: 0x18,
  FrameAck: 0x19,
  ReleaseAll: 0x1a,
} as const

export const Quality = { Smooth: 0, Sharp: 1 } as const
export type QualityPreset = (typeof Quality)[keyof typeof Quality]

export const SpecialKey = { CtrlAltDel: 1, Win: 2, PrintScreen: 3, TaskManager: 4, AltTab: 5 } as const
export type SpecialKeyId = (typeof SpecialKey)[keyof typeof SpecialKey]

export const StatusCode = { Ok: 0, SecureDesktop: 1, CaptureFailed: 2, Unsupported: 3 } as const

export interface MonitorInfo { index: number; width: number; height: number; primary: boolean }

export type AgentFrame =
  | { type: 'config'; codec: number; width: number; height: number; fps: number; monitor: number }
  | { type: 'video'; timestamp: number; key: boolean; data: Uint8Array }
  | { type: 'monitors'; monitors: MonitorInfo[] }
  | { type: 'clipboard'; text: string }
  | { type: 'stats'; fps: number; kbps: number; cpu: number; rtt: number }
  | { type: 'status'; code: number; message: string }

const utf8 = new TextDecoder()
const utf8Encoder = new TextEncoder()

/** 解析 Agent 发来的一帧；不认识或格式不对返回 null */
export function parseAgentFrame(buffer: ArrayBuffer): AgentFrame | null {
  const b = new Uint8Array(buffer)
  if (b.length === 0) return null
  const v = new DataView(buffer)
  switch (b[0]) {
    case FrameType.VideoConfig:
      if (b.length < 8) return null
      return { type: 'config', codec: b[1]!, width: v.getUint16(2, true), height: v.getUint16(4, true), fps: b[6]!, monitor: b[7]! }
    case FrameType.VideoFrame:
      if (b.length < 6) return null
      return { type: 'video', timestamp: v.getUint32(1, true), key: b[5] !== 0, data: b.subarray(6) }
    case FrameType.Monitors: {
      if (b.length < 2) return null
      const count = b[1]!
      if (b.length < 2 + count * 6) return null
      const monitors: MonitorInfo[] = []
      for (let i = 0; i < count; i++) {
        const o = 2 + i * 6
        monitors.push({ index: b[o]!, width: v.getUint16(o + 1, true), height: v.getUint16(o + 3, true), primary: b[o + 5] !== 0 })
      }
      return { type: 'monitors', monitors }
    }
    case FrameType.Clipboard:
      return { type: 'clipboard', text: utf8.decode(b.subarray(1)) }
    case FrameType.Stats:
      if (b.length < 7) return null
      return { type: 'stats', fps: b[1]!, kbps: v.getUint16(2, true), cpu: b[4]!, rtt: v.getUint16(5, true) }
    case FrameType.Status:
      if (b.length < 2) return null
      return { type: 'status', code: b[1]!, message: utf8.decode(b.subarray(2)) }
    default:
      return null
  }
}

function frame(type: number, size: number, fill?: (v: DataView, b: Uint8Array) => void): Uint8Array {
  const b = new Uint8Array(1 + size)
  b[0] = type
  fill?.(new DataView(b.buffer), b)
  return b
}

/** 剪贴板、文字一帧最多这么多 UTF-8 字节（与 C# RemoteFrames.MaxTextBytes 一致，Agent 收到更长的会整帧丢掉） */
export const MAX_TEXT_BYTES = 1 << 20

export function utf8Length(value: string): number {
  return utf8Encoder.encode(value).length
}

/** 超长时截断在字符边界上（不切断多字节字符） */
function text(type: number, value: string): Uint8Array {
  const bytes = utf8Encoder.encode(value)
  let length = bytes.length
  if (length > MAX_TEXT_BYTES) {
    length = MAX_TEXT_BYTES
    while (length > 0 && (bytes[length]! & 0xc0) === 0x80) length--
  }
  const b = new Uint8Array(1 + length)
  b[0] = type
  b.set(bytes.subarray(0, length), 1)
  return b
}

/** 控制端 → Agent */
export const encode = {
  mouseMove: (x: number, y: number) => frame(FrameType.MouseMove, 8, (v) => {
    v.setFloat32(1, x, true)
    v.setFloat32(5, y, true)
  }),
  /** button：浏览器 MouseEvent.button（0 左、1 中、2 右、3 后退、4 前进），与协议一致 */
  mouseButton: (button: number, down: boolean) => frame(FrameType.MouseButton, 2, (_, b) => {
    b[1] = button
    b[2] = down ? 1 : 0
  }),
  wheel: (dx: number, dy: number) => frame(FrameType.Wheel, 4, (v) => {
    v.setInt16(1, clamp16(dx), true)
    v.setInt16(3, clamp16(dy), true)
  }),
  key: (scanCode: number, down: boolean, modifiers = 0) => frame(FrameType.Key, 4, (v, b) => {
    v.setUint16(1, scanCode, true)
    b[3] = down ? 1 : 0
    b[4] = modifiers
  }),
  selectMonitor: (index: number) => frame(FrameType.SelectMonitor, 1, (_, b) => { b[1] = index }),
  setQuality: (preset: QualityPreset) => frame(FrameType.SetQuality, 1, (_, b) => { b[1] = preset }),
  requestKeyFrame: () => frame(FrameType.RequestKeyFrame, 0),
  specialKey: (id: SpecialKeyId) => frame(FrameType.SpecialKey, 1, (_, b) => { b[1] = id }),
  text: (value: string) => text(FrameType.Text, value),
  clipboard: (value: string) => text(FrameType.Clipboard, value),
  frameAck: (timestamp: number) => frame(FrameType.FrameAck, 4, (v) => v.setUint32(1, timestamp >>> 0, true)),
  releaseAll: () => frame(FrameType.ReleaseAll, 0),
}

function clamp16(n: number): number {
  return Math.max(-32768, Math.min(32767, Math.round(n)))
}

/**
 * KeyboardEvent.code（物理键位，与输入法、键盘布局无关）→ Windows 扫描码（集合 1），0xE0xx 是扩展键。
 * 按物理键发送：对方电脑上用什么输入法、什么布局，就按那边的来；要输入中文用"文字"输入框。
 * 注意 Agent 用 SendInput 的扫描码注入：NumLock 是不带 0xE0 的 0x45；Pause 需要 E1 1D 45 三字节序列，扫描码表达不了，不支持。
 */
export const SCAN_CODES: Readonly<Record<string, number>> = {
  Escape: 0x01, Digit1: 0x02, Digit2: 0x03, Digit3: 0x04, Digit4: 0x05, Digit5: 0x06, Digit6: 0x07, Digit7: 0x08,
  Digit8: 0x09, Digit9: 0x0a, Digit0: 0x0b, Minus: 0x0c, Equal: 0x0d, Backspace: 0x0e, Tab: 0x0f,
  KeyQ: 0x10, KeyW: 0x11, KeyE: 0x12, KeyR: 0x13, KeyT: 0x14, KeyY: 0x15, KeyU: 0x16, KeyI: 0x17, KeyO: 0x18, KeyP: 0x19,
  BracketLeft: 0x1a, BracketRight: 0x1b, Enter: 0x1c, ControlLeft: 0x1d,
  KeyA: 0x1e, KeyS: 0x1f, KeyD: 0x20, KeyF: 0x21, KeyG: 0x22, KeyH: 0x23, KeyJ: 0x24, KeyK: 0x25, KeyL: 0x26,
  Semicolon: 0x27, Quote: 0x28, Backquote: 0x29, ShiftLeft: 0x2a, Backslash: 0x2b,
  KeyZ: 0x2c, KeyX: 0x2d, KeyC: 0x2e, KeyV: 0x2f, KeyB: 0x30, KeyN: 0x31, KeyM: 0x32,
  Comma: 0x33, Period: 0x34, Slash: 0x35, ShiftRight: 0x36, NumpadMultiply: 0x37, AltLeft: 0x38, Space: 0x39, CapsLock: 0x3a,
  F1: 0x3b, F2: 0x3c, F3: 0x3d, F4: 0x3e, F5: 0x3f, F6: 0x40, F7: 0x41, F8: 0x42, F9: 0x43, F10: 0x44,
  NumLock: 0x45, ScrollLock: 0x46,
  Numpad7: 0x47, Numpad8: 0x48, Numpad9: 0x49, NumpadSubtract: 0x4a, Numpad4: 0x4b, Numpad5: 0x4c, Numpad6: 0x4d,
  NumpadAdd: 0x4e, Numpad1: 0x4f, Numpad2: 0x50, Numpad3: 0x51, Numpad0: 0x52, NumpadDecimal: 0x53,
  IntlBackslash: 0x56, F11: 0x57, F12: 0x58,
  NumpadEnter: 0xe01c, ControlRight: 0xe01d, NumpadDivide: 0xe035, PrintScreen: 0xe037, AltRight: 0xe038,
  Home: 0xe047, ArrowUp: 0xe048, PageUp: 0xe049, ArrowLeft: 0xe04b, ArrowRight: 0xe04d, End: 0xe04f,
  ArrowDown: 0xe050, PageDown: 0xe051, Insert: 0xe052, Delete: 0xe053,
  MetaLeft: 0xe05b, MetaRight: 0xe05c, ContextMenu: 0xe05d,
}

export function scanCodeOf(code: string): number | undefined {
  return SCAN_CODES[code]
}

export function modifiersOf(e: { shiftKey: boolean; ctrlKey: boolean; altKey: boolean; metaKey: boolean }): number {
  return (e.shiftKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.altKey ? 4 : 0) | (e.metaKey ? 8 : 0)
}

export interface Box { left: number; top: number; width: number; height: number }

/**
 * 画布按 object-fit: contain 显示：页面上的点 → 远端画面里的 0..1 坐标。落在上下 / 左右的黑边里返回 null。
 */
export function normalizePoint(clientX: number, clientY: number, box: Box, videoWidth: number, videoHeight: number): { x: number; y: number } | null {
  if (box.width <= 0 || box.height <= 0 || videoWidth <= 0 || videoHeight <= 0) return null
  const scale = Math.min(box.width / videoWidth, box.height / videoHeight)
  const shownW = videoWidth * scale
  const shownH = videoHeight * scale
  const left = box.left + (box.width - shownW) / 2
  const top = box.top + (box.height - shownH) / 2
  const x = (clientX - left) / shownW
  const y = (clientY - top) / shownH
  // 边缘留 1 个像素的余量（指针正好在最后一行 / 列上时，换算可能略大于 1）
  const ex = 1 / shownW
  const ey = 1 / shownH
  if (x < -ex || x > 1 + ex || y < -ey || y > 1 + ey) return null
  return { x: Math.min(1, Math.max(0, x)), y: Math.min(1, Math.max(0, y)) }
}

/**
 * 滚轮：浏览器的 delta（像素 / 行 / 页）→ Windows 的滚动量（120 为一格，向上 / 向右为正）。
 * 触控板的细碎滚动累积起来，攒够整数再发。
 */
export class WheelAccumulator {
  private x = 0
  private y = 0

  push(e: { deltaX: number; deltaY: number; deltaMode: number }): { dx: number; dy: number } | null {
    // 像素：Chrome / Edge 在 Windows 上一格约 100 像素；行：一格 3 行；页：一格算 3 格
    const unit = e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? 360 : 1.2
    this.x += e.deltaX * unit
    this.y -= e.deltaY * unit
    const dx = Math.trunc(this.x)
    const dy = Math.trunc(this.y)
    if (dx === 0 && dy === 0) return null
    this.x -= dx
    this.y -= dy
    return { dx, dy }
  }
}

/** 从带 SPS 的 Annex B 关键帧里取 WebCodecs 的 codec 字符串（avc1.PPCCLL） */
export function codecFromAnnexB(data: Uint8Array): string | null {
  for (let i = 0; i + 3 < data.length; i++) {
    if (data[i] !== 0 || data[i + 1] !== 0 || data[i + 2] !== 1) continue
    const nal = i + 3
    if ((data[nal]! & 0x1f) === 7 && nal + 3 < data.length) {
      const hex = (n: number) => n.toString(16).padStart(2, '0').toUpperCase()
      return `avc1.${hex(data[nal + 1]!)}${hex(data[nal + 2]!)}${hex(data[nal + 3]!)}`
    }
    i = nal - 1
  }
  return null
}
