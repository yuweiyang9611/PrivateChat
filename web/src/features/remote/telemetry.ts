// 远程协助遥测（docs/DESIGN.md"远程协助遥测"）的数据类型：照着 src/PrivateChat.Contracts/RemoteTelemetry.cs 手写
// （生成的 schema.d.ts 里还没有这几个接口）。JSON 是 camelCase，枚举是字符串，时间是 ISO 字符串。
// 只有性能数字、硬件和驱动型号、出错的说明：不含屏幕内容、键盘输入、剪贴板、窗口标题、文件名、用户名。

import type { RemoteSessionMode } from '@/stores/remote'

/** 画质档位（RemoteVideoMode） */
export type TelemetryVideoMode = 'Auto' | 'Smooth' | 'Clear' | 'Original' | 'Custom'

/** 每秒一个样本的统计（samples = 0 时其余都是 0） */
export interface TelemetryStat { samples: number; mean: number; min: number; p10: number; p50: number; p90: number; max: number }

/** 被控端这台电脑的硬件和能力：连上服务器后上报 */
export interface AgentTelemetrySnapshot {
  agentVersion: string
  collectedAt: string
  /** 程序版本、显卡和驱动、显示器算出来的指纹：没变就不重新探测编码器 / 抓屏 */
  fingerprint: string
  os: TelemetryOs
  cpu: TelemetryCpu
  memoryBytes: number
  gpus: TelemetryGpu[]
  displays: TelemetryDisplay[]
  encoders: TelemetryEncoder[]
  capture: TelemetryCapture
  /** 程序跑在远程桌面（RDP）会话里 */
  remoteDesktopSession: boolean
  /** 探测时出的问题 */
  problems: string[]
}

export interface TelemetryOs { productName: string; displayVersion: string; build: number; revision: number; edition: string; is64Bit: boolean }
export interface TelemetryCpu { name: string; cores: number; threads: number; baseMhz: number }
/** outputs：接在这张卡上的显示器（\\.\DISPLAY1 这种名字） */
export interface TelemetryGpu { name: string; vendorId: number; deviceId: number; driverVersion: string; dedicatedMemoryBytes: number; software: boolean; outputs: string[] }
/** adapter：这块显示器接在哪张显卡上（显卡名），查不到是 null */
export interface TelemetryDisplay { deviceName: string; width: number; height: number; refreshHz: number; dpiScalePercent: number; primary: boolean; adapter: string | null }

/** 一个 H.264 编码器（Media Foundation）：软件的那个和各家显卡的硬件编码器 */
export interface TelemetryEncoder {
  name: string
  hardware: boolean
  vendorId: number
  activated: boolean
  async: boolean
  /** 它接受的码率控制方式：CBR / VBR / Quality / LowDelayVBR / …… */
  rateControlModes: string[]
  /** 能"按质量"编 */
  qualityMode: boolean
  /** 能逐帧给量化参数 */
  perFrameQp: boolean
  lowLatency: boolean
  accepts1080p: boolean
  accepts4k: boolean
  test: TelemetryEncodeTest | null
  error: string | null
}

/** 实际编一小段测试画面（系统内存里的 NV12，1920×1080，30 帧） */
export interface TelemetryEncodeTest { width: number; height: number; frames: number; framesOut: number; firstFrameMs: number; avgMs: number; p90Ms: number; bytes: number; error: string | null }

/** gdiNativeMs / gdiScaledMs：GDI 按原尺寸截主显示器一帧平均多久、缩到 1280×720 一帧多久 */
export interface TelemetryCapture { duplication: TelemetryDuplication[]; gdiNativeMs: number | null; gdiScaledMs: number | null; error: string | null }
/** Desktop Duplication 在这块显示器上能不能用 */
export interface TelemetryDuplication {
  output: string; adapter: string | null; ok: boolean; firstFrameMs: number | null; error: string | null
  /** 0.8.7 起：画面在动时每幅新画面多久（显卡缩放转 NV12 + 读回），按自动档最大的尺寸量；老版本没有 */
  frameMs?: number | null; frameP90Ms?: number | null; frameSize?: string | null
}

export type IceCandidateType = 'host' | 'srflx' | 'prflx' | 'relay'

export interface PublisherAttemptTelemetry {
  attempt: number
  outcome: 'connected' | 'failed' | 'cancelled' | 'pending'
  strategy: 'all' | 'relay' | 'unknown'
  totalMs: number
  iceConfigMs: number | null
  gatherMs: number | null
  publishMs: number | null
  handshakeMs: number | null
  localCandidateType: IceCandidateType | null
  remoteCandidateType: IceCandidateType | null
}

export interface ViewerFastAttemptTelemetry {
  attempt: number
  outcome: 'firstFrame' | 'failed' | 'cancelled' | 'pending'
  totalMs: number
  publisherWaitMs: number | null
  pullToFirstFrameMs: number | null
}

export interface ViewerStartupTelemetry {
  requestToFirstFrameMs: number | null
  connectToFirstFrameMs: number | null
  firstFrameLine: 'relay' | 'realtime' | null
  attempts: ViewerFastAttemptTelemetry[]
}

export interface ViewerPlaybackTelemetry {
  fastFramesDecoded: number | null
  fastFramesDropped: number | null
  fastNackRequests: number | null
  fastRetransmittedPackets: number | null
  fastDecodeMs: TelemetryStat | null
  relayDecodeQueue: TelemetryStat | null
  relaySkippedUntilKey: number | null
}

/** 一次远程里被控端这边的汇总：每 60 秒一次（final = false），结束时一次 */
export interface AgentSessionTelemetry {
  agentVersion: string
  final: boolean
  videoSeconds: number
  relaySeconds: number
  realtimeSeconds: number
  /** 现在用的编码器、是不是按质量编、怎么截屏 */
  encoder: string
  qualityMode: boolean
  capture: string
  settings: TelemetrySettingsUse[]
  sizes: TelemetrySizeUse[]
  sentFps: TelemetryStat
  sentKbps: TelemetryStat
  targetKbps: TelemetryStat
  captureMs: TelemetryStat
  encodeMs: TelemetryStat
  cpuPercent: TelemetryStat
  rttMs: TelemetryStat
  cpuBoundSeconds: number
  secureDesktopSeconds: number
  autoNetworkSteps: number
  autoCpuSteps: number
  autoCpuUndos: number
  keyFramesRequested: number
  stalls: number
  relaySendDrops: number
  maxFpsCaps: number
  minMaxFps: number
  lineConnects: number
  lineFailures: number
  lineProblems: string[]
  publisherAttempts?: PublisherAttemptTelemetry[] | null
  /** 快速线路上控制端每秒的回报；没走快速线路时是 null */
  lossPermille: TelemetryStat | null
  receivedKbps: TelemetryStat | null
  jitterBufferMs: TelemetryStat | null
  viewerFps: TelemetryStat | null
  errors: string[]
}

export interface TelemetrySettingsUse { mode: TelemetryVideoMode; maxWidth: number; maxHeight: number; fps: number; maxKbps: number; seconds: number }
export interface TelemetrySizeUse { width: number; height: number; seconds: number }

/** 一次远程里控制端浏览器这边的汇总（看到的才是用户觉得卡不卡）：每 60 秒一次，结束 / 关页面时再一次 */
export interface ViewerSessionTelemetry {
  final: boolean
  browser: string
  platform: string
  screenWidth: number
  screenHeight: number
  devicePixelRatio: number
  hardwareConcurrency: number
  seconds: number
  relaySeconds: number
  realtimeSeconds: number
  decodedFps: TelemetryStat
  /** 该有画面却超过 500 ms 没出新帧算一次卡住：次数、最长一次、加起来多久 */
  freezes: number
  longestFreezeMs: number
  freezeMsTotal: number
  keyFrameRequests: number
  maxFpsCaps: number
  minMaxFps: number
  realtimeReconnects: number
  lineProblems: string[]
  startup?: ViewerStartupTelemetry | null
  playback?: ViewerPlaybackTelemetry | null
  jitterBufferMs: TelemetryStat | null
  lossPermille: TelemetryStat | null
  errors: string[]
}

/** 管理员看的：这台电脑的硬件和能力（没报过是 null），加上最近几次有遥测的远程（新的在前） */
export interface DeviceTelemetryDto {
  deviceId: string
  deviceName: string
  reportedAt: string | null
  snapshot: AgentTelemetrySnapshot | null
  sessions: SessionTelemetryDto[]
  recentUpdate?: RecentUpdateDto | null
}

export interface RecentUpdateDto {
  checkedAt: string
  receivedAt: string
  source: 'auto' | 'manual'
  currentVersion: string
  targetVersion: string | null
  state: 'checking' | 'available' | 'downloading' | 'verifying' | 'restarting' | 'current' | 'busy' | 'postponed' |
    'cancelled' | 'failed' | 'noManifest' | 'unavailable'
  elapsedMs: number
  failureStage: 'check' | 'download' | 'verify' | 'install' | null
  errorCode: 'network' | 'timeout' | 'integrity' | 'signature' | 'permission' | 'storage' | 'launch' | 'invalidManifest' | 'server' | 'unknown' | null
  confirmedAt: string | null
}

/** 一次远程：会话本身的记录，加上被控端、控制端各自报的汇总（哪边没报过就是 null） */
export interface SessionTelemetryDto {
  sessionId: string
  startedAt: string
  endedAt: string | null
  endReason: string | null
  mode: RemoteSessionMode
  controllerName: string
  updatedAt: string
  agent: AgentSessionTelemetry | null
  viewer: ViewerSessionTelemetry | null
}
