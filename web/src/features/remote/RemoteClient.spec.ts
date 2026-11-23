import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { RemoteClient } from './RemoteClient'
import { ViewerTelemetry } from './viewerTelemetry'
import { Features, FrameType, Transport, TransportState } from './protocol'

let paintFast: (video: HTMLVideoElement, mediaMs: number | null) => void
vi.mock('./RemoteRealtime', async (load) => ({
  ...await load<typeof import('./RemoteRealtime')>(),
  RemoteRealtime: class {
    constructor(_id: string, paint: typeof paintFast) { paintFast = paint }
    async start() {}
    close() {}
  },
}))

class AgentSocket {
  static OPEN = 1
  static current: AgentSocket
  readyState = AgentSocket.OPEN
  transports: number[] = []
  onmessage?: (event: { data: string | ArrayBuffer }) => void

  constructor() { AgentSocket.current = this }
  close() {}
  send(frame: Uint8Array) {
    if (frame[0] === FrameType.SetTransport) this.transports.push(frame[1]!)
  }
  config() {
    this.onmessage?.({ data: Uint8Array.of(FrameType.VideoConfig, 1, 128, 2, 224, 1, 30, 0, Features.Realtime).buffer })
  }
  fail() {
    this.onmessage?.({ data: Uint8Array.of(FrameType.Transport, TransportState.RealtimeFailed, 0).buffer })
  }
}

let client: RemoteClient
let telemetry: ViewerTelemetry
let agent: AgentSocket
const line = vi.fn()
beforeEach(() => {
  vi.useFakeTimers()
  vi.stubGlobal('WebSocket', AgentSocket)
  vi.stubGlobal('RTCPeerConnection', class {})
  const canvas = document.createElement('canvas')
  Object.defineProperty(canvas, 'getContext', { value: () => ({ drawImage: vi.fn() }) })
  telemetry = new ViewerTelemetry()
  client = new RemoteClient('demo', canvas, { line }, telemetry)
  client.setTransport(true)
  client.connect()
  agent = AgentSocket.current
  agent.onmessage?.({ data: JSON.stringify({ type: 'peer', connected: true }) })
  agent.config()
})

afterEach(() => {
  client.close()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  vi.useRealTimers()
  line.mockClear()
})

it('收到视频配置或排队解码都不算出图，手动切回中继后实际绘制才记录首帧', () => {
  let output: VideoFrameOutputCallback
  vi.stubGlobal('VideoDecoder', class {
    state = 'configured'
    constructor(init: VideoDecoderInit) { output = init.output }
    configure() {}
    decode() {}
    close() {}
  })
  vi.stubGlobal('EncodedVideoChunk', class {})
  vi.advanceTimersByTime(100)
  agent.fail()
  vi.advanceTimersByTime(300)
  vi.advanceTimersByTime(50)
  client.setTransport(false)
  agent.onmessage?.({ data: Uint8Array.of(FrameType.VideoFrame, 1, 0, 0, 0, 1, 0, 0, 1, 0x67, 0x4d, 0, 0x1f).buffer })
  expect(telemetry.startup.snapshot(performance.now())?.firstFrameLine).toBeNull()
  vi.advanceTimersByTime(12)
  output!({ timestamp: 1000, displayWidth: 640, displayHeight: 480, close() {} } as VideoFrame)
  expect(telemetry.startup.snapshot(performance.now())).toEqual({
    firstFrameLine: 'relay', connectToFirstFrameMs: 462, requestToFirstFrameMs: null,
    attempts: [
      { attempt: 1, totalMs: 100, outcome: 'failed', publisherWaitMs: 100, pullToFirstFrameMs: null },
      { attempt: 2, totalMs: 50, outcome: 'cancelled', publisherWaitMs: 50, pullToFirstFrameMs: null },
    ],
  })
})

it('快速线路发布就绪开始计拉流时间，画到 canvas 后才完成这次尝试', () => {
  vi.advanceTimersByTime(400)
  agent.onmessage?.({ data: Uint8Array.of(FrameType.Transport, TransportState.Realtime, 1).buffer })
  vi.advanceTimersByTime(125)
  const video = document.createElement('video')
  paintFast(video, null)
  expect(telemetry.startup.snapshot(performance.now())).toMatchObject({
    firstFrameLine: null, attempts: [{ attempt: 1, outcome: 'pending', totalMs: 525, publisherWaitMs: 400, pullToFirstFrameMs: 125 }],
  })
  Object.defineProperties(video, { videoWidth: { value: 640 }, videoHeight: { value: 480 } })
  paintFast(video, null)
  expect(telemetry.startup.snapshot(performance.now())).toMatchObject({
    firstFrameLine: 'realtime', connectToFirstFrameMs: 525,
    attempts: [{ attempt: 1, outcome: 'firstFrame', totalMs: 525, publisherWaitMs: 400, pullToFirstFrameMs: 125 }],
  })
})

it('首次接通失败后在 300 毫秒重试，连续失败时延长等待，配置通知和切回页面都不会抢跑', () => {
  expect(agent.transports).toEqual([Transport.Realtime])
  agent.fail()
  vi.advanceTimersByTime(299)
  expect(agent.transports).toHaveLength(1)
  vi.advanceTimersByTime(1)
  expect(agent.transports).toEqual([Transport.Realtime, Transport.Realtime])
  agent.fail()
  vi.advanceTimersByTime(1000)
  agent.config()
  document.dispatchEvent(new Event('visibilitychange'))
  expect(agent.transports).toHaveLength(2)
  vi.advanceTimersByTime(1000)
  expect(agent.transports).toEqual(Array(3).fill(Transport.Realtime))
})

it('退避到期时页面在后台，回到前台才发起一次握手', () => {
  const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
  agent.fail()
  vi.advanceTimersByTime(1000)
  expect(agent.transports).toHaveLength(1)
  visibility.mockReturnValue('visible')
  document.dispatchEvent(new Event('visibilitychange'))
  agent.config()
  expect(agent.transports).toEqual([Transport.Realtime, Transport.Realtime])
})

it('用户可以提前重试，握手中连点和旧的倒计时都不会再发请求', () => {
  agent.fail()
  client.retryRealtime()
  expect(line).toHaveBeenLastCalledWith('connecting')
  client.retryRealtime()
  vi.advanceTimersByTime(1000)
  expect(agent.transports).toEqual([Transport.Realtime, Transport.Realtime])
})

it.each(['切到标准', '关闭会话'])('%s 后取消还在等待的重试', (action) => {
  agent.fail()
  if (action === '切到标准') client.setTransport(false)
  else client.close()
  vi.advanceTimersByTime(30_000)
  document.dispatchEvent(new Event('visibilitychange'))
  expect(agent.transports).toEqual(action === '切到标准' ? [Transport.Realtime, Transport.Relay] : [Transport.Realtime])
})

it('五次自动重试耗尽后保持快速线路的失败提示，用户可重新开始', () => {
  for (const delay of [300, 2000, 5000, 10000, 15000]) {
    agent.fail()
    vi.advanceTimersByTime(delay)
  }
  agent.fail()
  expect(line).toHaveBeenLastCalledWith('connecting', expect.objectContaining({ retrying: false }))
  vi.advanceTimersByTime(60_000)
  agent.config()
  expect(agent.transports).toEqual(Array(6).fill(Transport.Realtime))
  client.retryRealtime()
  expect(agent.transports).toEqual(Array(7).fill(Transport.Realtime))
})
