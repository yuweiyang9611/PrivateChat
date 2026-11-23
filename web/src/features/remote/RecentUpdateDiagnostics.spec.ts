import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import { i18n } from '@/i18n'
import RecentUpdateDiagnostics from './RecentUpdateDiagnostics.vue'
import type { RecentUpdateDto } from './telemetry'

const sample: RecentUpdateDto = {
  checkedAt: '2026-11-24T02:05:00Z', receivedAt: '2026-11-24T02:05:01Z',
  source: 'manual', currentVersion: '0.8.10', targetVersion: '0.8.11', state: 'restarting',
  elapsedMs: 0, failureStage: null, errorCode: null, confirmedAt: null,
}

async function render(update?: RecentUpdateDto | null) {
  const app = createSSRApp({ render: () => h(RecentUpdateDiagnostics, { update }) })
  app.use(i18n)
  return renderToString(app)
}

describe('管理员的最近更新检查', () => {
  it('旧记录和未上报的设备不会显示成功或伪造时间', async () => {
    for (const absent of [undefined, null]) {
      const text = await render(absent)
      expect(text).toContain('设备未上报更新检查记录')
      expect(text).not.toContain('已更新，目标版本已上线')
      expect(text).not.toContain('0 ms')
    }
  })

  it('区分下载、重启等待、同版本检查和已上线确认', async () => {
    expect(await render({ ...sample, state: 'downloading' })).toContain('正在下载')
    const waiting = await render(sample)
    expect(waiting).toContain('等待目标版本上线')
    expect(waiting).toContain('0 ms')
    expect(waiting).toContain('手动检查')
    expect(waiting).not.toContain('已更新，目标版本已上线')
    const current = await render({ ...sample, state: 'current', currentVersion: '0.8.11' })
    expect(current).toContain('检查时已是最新版本')
    expect(current).not.toContain('已更新，目标版本已上线')
    const confirmed = await render({ ...sample, confirmedAt: '2026-11-24T02:05:10Z' })
    expect(confirmed).toContain('已更新，目标版本已上线')
    expect(confirmed).toContain('服务器确认上线')
  })

  it('以可读分类显示错误、检查来源、版本和耗时', async () => {
    const text = await render({ ...sample, source: 'auto', state: 'failed', elapsedMs: 60400, failureStage: 'download', errorCode: 'timeout' })
    for (const expected of ['自动检查', '更新失败', '下载安装包', '等待超时', 'v0.8.10', 'v0.8.11', '60.4 s']) {
      expect(text).toContain(expected)
    }
    expect(await render({ ...sample, state: 'failed', failureStage: 'check', errorCode: 'permission' })).toContain('权限不足')
  })
})
