import { expect, it } from 'vitest'
import { FastPlaybackCounters } from './playbackCounters'
import { ViewerTelemetry } from './viewerTelemetry'

const env = { browser: 'Edge', platform: 'Windows', screenWidth: 800, screenHeight: 600, devicePixelRatio: 1, hardwareConcurrency: 4 }
const stream = { type: 'inbound-rtp', kind: 'video', id: 'test-inbound' }

it('累计值只记增量，忽略迟到的采样，区分缺测、真零和计数器重置', () => {
  const collector = new FastPlaybackCounters()
  const observe = (timestamp: number, values: Record<string, number>) => collector.observe({ ...stream, timestamp, ...values })
  expect(observe(1, { framesDecoded: 10, framesDropped: 0, totalDecodeTime: 0.1 })).toEqual(
    { decoded: 10, dropped: 0, nack: null, retransmitted: null, decodeMs: 10 })
  expect(observe(1, { framesDropped: 6 })).toBeNull()
  expect(observe(2, { framesDropped: 2, nackCount: 3 })?.decoded).toBeNull()
  const next = observe(3, { framesDecoded: 15, totalDecodeTime: 0.2, framesDropped: 2, nackCount: 5, retransmittedPacketsReceived: 4 })
  expect(next).toEqual({ decoded: 5, dropped: 0, nack: 2, retransmitted: 4, decodeMs: 20 })
  expect(observe(4, { framesDecoded: 1, totalDecodeTime: 0.008, framesDropped: 0 })?.decodeMs).toBe(8)
  expect(collector.observe({ ...stream, id: 'other-video', timestamp: 1, framesDecoded: 2 })?.decoded).toBe(2)
  expect(collector.observe({ ...stream, kind: 'audio', timestamp: 5, nackCount: 30 })).toBeNull()
})

it('播放统计可跨重连相加、刷新后接续，旧版记录缺少测量时保留空值', () => {
  const t = ViewerTelemetry.restore({ v: 1 })!
  expect(t.snapshot(0, env, false).playback?.fastFramesDropped).toBeNull()
  expect(t.snapshot(0, env, false).playback?.relaySkippedUntilKey).toBeNull()
  t.relayQueue(0)
  expect(t.snapshot(0, env, false).playback?.relaySkippedUntilKey).toBe(0)
  t.relaySkippedFrame()
  t.fastPlayback({ decoded: 10, dropped: 0, nack: null, retransmitted: 2, decodeMs: 3.5 })
  const restored = ViewerTelemetry.restore(JSON.parse(JSON.stringify(t.toJSON())))!
  restored.fastPlayback({ decoded: 5, dropped: 3, nack: null, retransmitted: 0, decodeMs: 7.5 })
  const p = restored.snapshot(0, env, true).playback!
  expect([p.fastFramesDecoded, p.fastFramesDropped, p.fastNackRequests, p.fastRetransmittedPackets]).toEqual([15, 3, null, 2])
  expect([p.fastDecodeMs?.samples, p.fastDecodeMs?.mean, p.relayDecodeQueue?.max, p.relaySkippedUntilKey]).toEqual([2, 5.5, 0, 1])
})
