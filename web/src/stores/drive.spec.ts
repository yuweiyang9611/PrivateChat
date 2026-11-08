// 网盘 store：离线副本什么时候保留、什么时候作废；出错后刷新 / 重连能恢复；推送刷新的合并节流。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'

type Reply = { data?: unknown; error?: unknown; response: { ok: boolean; status: number } } | Error

const ctl = vi.hoisted(() => ({
  /** /api/drive/list 依次返回的结果；空了就用 fallback */
  list: [] as Reply[],
  fallback: null as Reply | null,
  listCalls: 0,
  overviewCalls: 0,
}))

vi.mock('@/api/client', () => ({
  api: {
    GET: async (path: string) => {
      if (path === '/api/drive') {
        ctl.overviewCalls++
        return { data: { spaces: [] }, response: { ok: true, status: 200 } }
      }
      if (path === '/api/drive/list') {
        ctl.listCalls++
        const r = ctl.list.shift() ?? ctl.fallback
        if (!r) throw new TypeError('Failed to fetch')
        if (r instanceof Error) throw r
        return r
      }
      throw new TypeError('Failed to fetch')
    },
  },
}))

const { useDriveStore, listingKey, CHANGE_DELAY_MS, CHANGE_MIN_GAP_MS } = await import('./drive')
const { db } = await import('@/db')

const SPACE = 'space-1'
const FOLDER = 'folder-1'
const KEY = listingKey(SPACE, FOLDER)

function listing(name: string) {
  return {
    space: { id: SPACE, type: 'Family', name: '家庭', permission: 'Write' },
    folder: { id: FOLDER, name: '旅行' },
    path: [{ id: FOLDER, name: '旅行' }],
    permission: 'Write',
    items: [{ id: 'n1', name }],
  }
}
const ok = (data: unknown): Reply => ({ data, response: { ok: true, status: 200 } })
const fail = (status: number, detail?: string): Reply => ({ error: detail ? { detail } : undefined, response: { ok: false, status } })
const itemNames = (d: ReturnType<typeof useDriveStore>) => (d.listing?.items ?? []).map((i) => i.name)

beforeEach(async () => {
  setActivePinia(createPinia())
  Object.assign(ctl, { list: [], fallback: null, listCalls: 0, overviewCalls: 0 })
  await db.driveListings.clear()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('网盘目录的离线副本', () => {
  it('家里服务器关机 / 隧道断了（5xx、530）：保留离线副本照常显示', async () => {
    await db.driveListings.put({ key: KEY, listing: listing('缓存里的.jpg'), savedAt: 1 })
    for (const status of [502, 530]) {
      setActivePinia(createPinia())
      const d = useDriveStore()
      ctl.list = [fail(status)]
      await d.open(SPACE, FOLDER)
      expect(itemNames(d)).toEqual(['缓存里的.jpg'])
      expect(d.offlineCopy).toBe(true)
      expect(d.error).toBeNull()
      expect(await db.driveListings.get(KEY)).toBeTruthy()
    }
  })

  it('服务器明确说没有 / 没权限（404、403）：离线副本作废，显示原因', async () => {
    await db.driveListings.put({ key: KEY, listing: listing('缓存里的.jpg'), savedAt: 1 })
    const d = useDriveStore()
    ctl.list = [fail(404, '文件夹不存在')]
    await d.open(SPACE, FOLDER)
    expect(d.listing).toBeNull()
    expect(d.error).toBe('文件夹不存在')
    expect(await db.driveListings.get(KEY)).toBeUndefined()
  })

  it('离线、没有副本时出错；之后刷新 / 恢复联网仍知道该刷哪个目录', async () => {
    const d = useDriveStore()
    ctl.list = [new TypeError('Failed to fetch')]
    await d.open(SPACE, FOLDER)
    expect(d.listing).toBeNull()
    expect(d.error).toBe('offline')

    ctl.list = [ok(listing('a.jpg'))]
    await d.refresh()
    expect(itemNames(d)).toEqual(['a.jpg'])
    expect(d.error).toBeNull()

    // 重连（resync）：概览和当前目录都重新拉，后台刷新不转圈
    ctl.list = [ok(listing('b.jpg'))]
    d.resync()
    expect(d.loading).toBe(false)
    await vi.waitFor(() => expect(itemNames(d)).toEqual(['b.jpg']))
    expect(ctl.overviewCalls).toBe(1)
  })

  it('没打开过网盘时重连什么也不做', () => {
    useDriveStore().resync()
    expect(ctl.listCalls).toBe(0)
    expect(ctl.overviewCalls).toBe(0)
  })
})

describe('DriveChanged 推送刷新', () => {
  it('持续有通知时合并刷新：第一次很快，之后最多每 1.5 秒一次', async () => {
    const d = useDriveStore()
    ctl.fallback = ok(listing('a.jpg'))
    await d.open(SPACE, FOLDER)
    const base = ctl.listCalls

    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] })
    const changed = () => d.onChanged({ spaceId: SPACE, parentId: FOLDER })
    changed()
    changed()
    await vi.advanceTimersByTimeAsync(CHANGE_DELAY_MS)
    expect(ctl.listCalls - base).toBe(1)

    // 批量上传：每 100 ms 一个文件登记完
    for (let i = 0; i < 10; i++) {
      changed()
      await vi.advanceTimersByTimeAsync(100)
    }
    // 上一次刷新后 1.0 s：还没到最小间隔
    expect(ctl.listCalls - base).toBe(1)
    await vi.advanceTimersByTimeAsync(CHANGE_MIN_GAP_MS)
    expect(ctl.listCalls - base).toBe(2)
    expect(d.loading).toBe(false)
  })
})
