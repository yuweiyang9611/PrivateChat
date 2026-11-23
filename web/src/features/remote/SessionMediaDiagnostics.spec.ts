import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import { i18n } from '@/i18n'
import SessionMediaDiagnostics from './SessionMediaDiagnostics.vue'
import type { AgentSessionTelemetry, PublisherAttemptTelemetry, SessionTelemetryDto, TelemetryStat, ViewerSessionTelemetry } from './telemetry'

const zeroStat: TelemetryStat = { samples: 1, mean: 0, min: 0, p10: 0, p50: 0, p90: 0, max: 0 }
const publisher: PublisherAttemptTelemetry = {
  attempt: 3, outcome: 'failed', strategy: 'relay', totalMs: 8500,
  iceConfigMs: 0, gatherMs: 500, publishMs: null, handshakeMs: 8000,
  localCandidateType: null, remoteCandidateType: null,
}

async function display(agent: Partial<AgentSessionTelemetry> | null, viewer: Partial<ViewerSessionTelemetry> | null) {
  const session = { agent, viewer } as SessionTelemetryDto
  const app = createSSRApp({ render: () => h(SessionMediaDiagnostics, { session }) })
  app.use(i18n)
  const container = document.createElement('div')
  container.innerHTML = await renderToString(app)
  return container
}

function reading(root: Element, label: string): string | null {
  return [...root.querySelectorAll('dt')].find(dt => dt.textContent === label)?.nextElementSibling?.textContent ?? null
}

describe('会话诊断详情', () => {
  it('老版本未上报启动和播放指标，已有帧率和明确的零次卡顿仍可见', async () => {
    const root = await display({ sentFps: { ...zeroStat, p50: 24, p10: 12 } }, { freezes: 0 })
    expect(root.textContent).toContain('控制端未上报首帧计时')
    expect(root.textContent).toContain('控制端未上报分线路播放指标')
    expect(reading(root, '发送帧率 P50 / P10')).toBe('24 / 12')
    expect(reading(root, '卡顿次数')).toBe('0')
    expect(reading(root, '丢弃帧数')).toBeNull()
  })

  it('按两端自己的尝试编号和时钟展示，区分尝试 TURN 与实际选中 TURN', async () => {
    const root = await display({ publisherAttempts: [publisher, {
      ...publisher, attempt: 4, outcome: 'connected', strategy: 'all', localCandidateType: 'relay', remoteCandidateType: 'host',
    }] }, {
      startup: {
        requestToFirstFrameMs: null, connectToFirstFrameMs: 0, firstFrameLine: 'relay',
        attempts: [{ attempt: 1, outcome: 'failed', totalMs: 3500, publisherWaitMs: 1200, pullToFirstFrameMs: 2300 }],
      },
    })
    expect(reading(root, '发起请求到首帧')).toBe('未记录')
    expect(reading(root, '开始连接到首帧')).toBe('0 ms')
    expect(reading(root, '首帧线路')).toBe('标准线路')
    const [first, second, viewer] = root.querySelectorAll('li')
    expect(first?.textContent).toContain('第 3 次')
    expect(first?.textContent).toContain('仅尝试 TURN')
    expect(first?.textContent).toContain('实际选中路径 · 未记录')
    expect(second?.textContent).toContain('经过 TURN（本地 relay → 对端 host）')
    expect(viewer?.textContent).toContain('第 1 次')
    expect(reading(viewer!, '拉流后等待首帧')).toBe('2.3 s')
    expect(viewer?.textContent).not.toContain('已收到首帧')
  })

  it('只显示合法候选类型，并区分缺了一端和已确认未经过 TURN', async () => {
    const root = await display({ publisherAttempts: [
      { ...publisher, localCandidateType: 'host', remoteCandidateType: null },
      { ...publisher, localCandidateType: 'srflx', remoteCandidateType: 'prflx' },
      { ...publisher, localCandidateType: '192.0.2.1' as never, remoteCandidateType: null },
    ] }, null)
    expect(root.textContent).toContain('TURN 使用情况未知')
    expect(root.textContent).toContain('未经过 TURN（本地 srflx → 对端 prflx）')
    expect(root.textContent).not.toContain('192.0.2.1')
  })

  it('已上报零次快速线路尝试；播放统计的缺测、零值与零样本不混淆', async () => {
    const root = await display({ publisherAttempts: [] }, {
      startup: { requestToFirstFrameMs: null, connectToFirstFrameMs: null, firstFrameLine: null, attempts: [] },
      playback: { fastFramesDecoded: 42, fastFramesDropped: 0, fastNackRequests: 1, fastRetransmittedPackets: null,
        fastDecodeMs: { ...zeroStat, samples: 0 }, relayDecodeQueue: zeroStat, relaySkippedUntilKey: null },
    })
    expect(root.textContent).toContain('没有快速线路尝试')
    expect(reading(root, '解码帧数')).toBe('42')
    expect(reading(root, '丢弃帧数')).toBe('0')
    expect(reading(root, '收到的重传包')).toBe('未记录')
    expect(reading(root, '每帧解码 ms P50 / P90')).toBe('未记录')
    expect(reading(root, '解码队列深度 P50 / P90')).toBe('0 / 0')
  })
})
