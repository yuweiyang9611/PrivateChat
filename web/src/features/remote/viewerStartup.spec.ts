import { describe, expect, it } from 'vitest'
import { ViewerTelemetry } from './viewerTelemetry'
import { requestedSessionRoute, requestTimeInDocument } from './viewerStartup'

describe('控制端起播计时', () => {
  it('从点击和首次连接计时，失败重试的时间计入总等待，实际画出首帧才算成功', () => {
    const t = new ViewerTelemetry().startup
    t.requested(0)
    t.connecting(320)
    t.fastRequested(500)
    t.finish(8500, 'failed')
    t.connecting(8600)
    t.fastRequested(8800)
    t.pulling(9500)
    expect(t.snapshot(9600)).toMatchObject({ firstFrameLine: null, connectToFirstFrameMs: null, requestToFirstFrameMs: null })
    t.painted(9900, 'realtime')
    t.painted(9950, 'realtime')
    t.painted(10_000, 'relay')
    expect(t.snapshot(10_000)).toEqual({ firstFrameLine: 'realtime', requestToFirstFrameMs: 9900, connectToFirstFrameMs: 9580,
      attempts: [
        { attempt: 1, outcome: 'failed', totalMs: 8000, publisherWaitMs: 8000, pullToFirstFrameMs: null },
        { attempt: 2, outcome: 'firstFrame', totalMs: 1100, publisherWaitMs: 700, pullToFirstFrameMs: 400 },
      ] })
  })

  it('结束或换线路时区分取消和失败；被控端自行重新发布时不虚构等它发布的耗时', () => {
    const t = new ViewerTelemetry().startup
    t.connecting(0)
    t.fastRequested(5)
    t.pulling(50)
    t.finish(75, 'cancelled')
    t.painted(99, 'relay')
    t.pulling(100)
    t.finish(150, 'failed')
    expect(t.snapshot(160)).toEqual({ firstFrameLine: 'relay', requestToFirstFrameMs: null, connectToFirstFrameMs: 99,
      attempts: [
        { attempt: 1, outcome: 'cancelled', totalMs: 70, publisherWaitMs: 45, pullToFirstFrameMs: 25 },
        { attempt: 2, outcome: 'failed', totalMs: 50, publisherWaitMs: null, pullToFirstFrameMs: 50 },
      ] })
  })

  it('页面刷新保留已完成时长，在旧文档里未结束的尝试以已观测的时长取消', () => {
    const t = new ViewerTelemetry()
    t.startup.requested(40_000)
    t.startup.connecting(40_200)
    t.startup.painted(40_320, 'relay')
    t.startup.fastRequested(41_000)
    t.startup.pulling(41_400)
    const saved = t.save(41_500)
    expect(saved.startup?.attempts[0]).toMatchObject({ outcome: 'pending', totalMs: 500 })
    const restored = ViewerTelemetry.restore(JSON.parse(JSON.stringify(saved)))!
    restored.startup.connecting(3)
    restored.startup.fastRequested(5)
    restored.startup.pulling(30)
    restored.startup.painted(50, 'realtime')
    expect(restored.startup.snapshot(50)).toEqual({ firstFrameLine: 'relay', requestToFirstFrameMs: 320, connectToFirstFrameMs: 120,
      attempts: [
        { attempt: 1, outcome: 'cancelled', totalMs: 500, publisherWaitMs: 400, pullToFirstFrameMs: 100 },
        { attempt: 2, outcome: 'firstFrame', totalMs: 45, publisherWaitMs: 25, pullToFirstFrameMs: 20 },
      ] })
    const route = requestedSessionRoute('demo', 40_000)
    expect(requestTimeInDocument(route.state, 'demo', performance.timeOrigin)).toBe(40_000)
    expect(requestTimeInDocument(route.state, 'demo', performance.timeOrigin + 1)).toBeNull()
    expect(requestTimeInDocument(route.state, 'other', performance.timeOrigin)).toBeNull()
    expect(requestTimeInDocument(null, 'demo', performance.timeOrigin)).toBeNull()
  })

  it('只留最近八次，刷新后序号仍然接续', () => {
    const t = new ViewerTelemetry()
    for (let i = 0; i < 10; i++) t.startup.fastRequested(i * 100)
    const a = t.startup.snapshot(950)!.attempts
    expect(a.map(row => row.attempt)).toEqual([3, 4, 5, 6, 7, 8, 9, 10])
    const restored = ViewerTelemetry.restore(t.save(950))!
    restored.startup.fastRequested(1)
    expect(restored.startup.snapshot(5)!.attempts.map(row => row.attempt)).toEqual([4, 5, 6, 7, 8, 9, 10, 11])
  })

  it('在第一帧前刷新，仍可记录最终线路，但不会用新页面的连接冒充第一次连接', () => {
    const t = new ViewerTelemetry()
    t.startup.requested(10_000)
    t.startup.connecting(11_000)
    const restored = ViewerTelemetry.restore(t.save(12_000))!
    restored.startup.connecting(50)
    restored.startup.painted(90, 'relay')
    expect(restored.startup.snapshot(100)).toMatchObject({
      firstFrameLine: 'relay', requestToFirstFrameMs: null, connectToFirstFrameMs: null,
    })
  })
})
