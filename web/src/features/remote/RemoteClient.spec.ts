import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { RemoteClient } from './RemoteClient'
import { Features, FrameType, Transport, TransportState } from './protocol'

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
let agent: AgentSocket
const line = vi.fn()
beforeEach(() => {
  vi.useFakeTimers()
  vi.stubGlobal('WebSocket', AgentSocket)
  vi.stubGlobal('RTCPeerConnection', class {})
  const canvas = document.createElement('canvas')
  Object.defineProperty(canvas, 'getContext', { value: () => null })
  client = new RemoteClient('demo', canvas, { line })
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
