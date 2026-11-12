// 时光 store：时间线的插入位置、实时事件的合并与删除、重连补拉。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'

const ctl = vi.hoisted(() => ({
  /** 第一页 / 后续页的回复，按顺序取 */
  pages: [] as unknown[],
  /** GET /api/posts/{id} 的结果：id → post，不在里面就 404 */
  byId: {} as Record<string, unknown>,
  getCalls: [] as string[],
}))

vi.mock('@/api/client', () => ({
  api: {
    GET: async (path: string, init?: { params?: { path?: { id?: string } } }) => {
      if (path === '/api/posts') return { data: ctl.pages.shift(), response: { ok: true, status: 200 } }
      if (path === '/api/posts/{id}') {
        const id = init?.params?.path?.id ?? ''
        ctl.getCalls.push(id)
        const p = ctl.byId[id]
        return p ? { data: p, response: { ok: true, status: 200 } } : { error: { title: '动态不存在' }, response: { ok: false, status: 404 } }
      }
      throw new TypeError('Failed to fetch')
    },
  },
}))

const { useMomentsStore, newerFirst } = await import('./moments')

function post(id: string, minute: number, text = id) {
  return {
    id, authorId: 'u1', text, visibility: 'Family' as const,
    createdAt: new Date(Date.UTC(2026, 10, 12, 8, minute)).toISOString(),
    media: [], reactions: [], comments: [],
  }
}

beforeEach(() => {
  setActivePinia(createPinia())
  ctl.pages = []
  ctl.byId = {}
  ctl.getCalls = []
  vi.useFakeTimers()
})
afterEach(() => vi.useRealTimers())

describe('时光时间线', () => {
  it('新的在前；时间相同时按 id 倒序（与服务端游标一致）', () => {
    const list = [post('a', 1), post('c', 5), post('b', 5)].sort(newerFirst)
    expect(list.map((p) => p.id)).toEqual(['c', 'b', 'a'])
  })

  it('别人发的新动态实时插到最前面；多个事件 300 ms 内只取一次', async () => {
    const m = useMomentsStore()
    ctl.pages.push({ items: [post('p2', 2), post('p1', 1)], nextBefore: null, nextBeforeId: null })
    await m.loadFirst()

    ctl.byId.p3 = post('p3', 3)
    m.onPostChanged({ postId: 'p3', authorId: 'u2', change: 'created' })
    m.onPostChanged({ postId: 'p3', authorId: 'u2', change: 'updated' })
    await vi.advanceTimersByTimeAsync(350)
    expect(ctl.getCalls).toEqual(['p3'])
    expect(m.posts.map((p) => p.id)).toEqual(['p3', 'p2', 'p1'])
  })

  it('还没翻到的更老动态不插进时间线（翻页时自然会加载）', async () => {
    const m = useMomentsStore()
    ctl.pages.push({ items: [post('p5', 5), post('p4', 4)], nextBefore: '2026-11-12T08:04:00Z', nextBeforeId: 'p4' })
    await m.loadFirst()
    m.upsert(post('old', 1), { insert: true })
    expect(m.posts.map((p) => p.id)).toEqual(['p5', 'p4'])
    expect(m.cache.old).toBeDefined()
  })

  it('评论 / 点赞事件替换已有的那条；删掉的、取不到（404）的从时间线移除', async () => {
    const m = useMomentsStore()
    ctl.pages.push({ items: [post('p2', 2), post('p1', 1)], nextBefore: null, nextBeforeId: null })
    await m.loadFirst()

    ctl.byId.p2 = { ...post('p2', 2), comments: [{ id: 'c1', postId: 'p2', authorId: 'u2', text: '好', createdAt: '', reactions: [] }] }
    m.onPostChanged({ postId: 'p2', authorId: 'u1', change: 'updated' })
    await vi.advanceTimersByTimeAsync(350)
    expect(m.posts[0]!.comments).toHaveLength(1)

    m.onPostChanged({ postId: 'p1', authorId: 'u1', change: 'updated' }) // 已经变成私密 / 被删：404
    await vi.advanceTimersByTimeAsync(350)
    expect(m.posts.map((p) => p.id)).toEqual(['p2'])

    m.onPostChanged({ postId: 'p2', authorId: 'u1', change: 'deleted' })
    expect(m.posts).toEqual([])
    expect(m.cache.p2).toBeUndefined()
  })

  it('没打开过时光页时不为实时事件发请求', async () => {
    const m = useMomentsStore()
    m.onPostChanged({ postId: 'x', authorId: 'u2', change: 'created' })
    await vi.advanceTimersByTimeAsync(350)
    expect(ctl.getCalls).toEqual([])
  })

  it('重连后合并最新一页，不丢已经往下翻出来的内容', async () => {
    const m = useMomentsStore()
    ctl.pages.push({ items: [post('p3', 3), post('p2', 2)], nextBefore: '2026-11-12T08:02:00Z', nextBeforeId: 'p2' })
    await m.loadFirst()
    ctl.pages.push({ items: [post('p1', 1)], nextBefore: null, nextBeforeId: null })
    await m.loadMore()
    expect(m.posts.map((p) => p.id)).toEqual(['p3', 'p2', 'p1'])

    ctl.pages.push({ items: [post('p4', 4), post('p3', 3, '改过了')], nextBefore: '2026-11-12T08:03:00Z', nextBeforeId: 'p3' })
    m.resync()
    await vi.advanceTimersByTimeAsync(0)
    expect(m.posts.map((p) => p.id)).toEqual(['p4', 'p3', 'p2', 'p1'])
    expect(m.posts[1]!.text).toBe('改过了')
  })

  it('断线期间被删掉的动态，重连补拉后从时间线去掉', async () => {
    const m = useMomentsStore()
    ctl.pages.push({ items: [post('p3', 3), post('p2', 2), post('p1', 1)], nextBefore: null, nextBeforeId: null })
    await m.loadFirst()
    ctl.pages.push({ items: [post('p3', 3), post('p1', 1)], nextBefore: null, nextBeforeId: null })
    m.resync()
    await vi.advanceTimersByTimeAsync(0)
    expect(m.posts.map((p) => p.id)).toEqual(['p3', 'p1'])
  })

  it('重新加载第一页时，还在路上的翻页结果作废（不会接出一段缺口）', async () => {
    const m = useMomentsStore()
    ctl.pages.push({ items: [post('p9', 9)], nextBefore: '2026-11-12T08:09:00Z', nextBeforeId: 'p9' })
    await m.loadFirst()
    ctl.pages.push({ items: [post('p8', 8)], nextBefore: '2026-11-12T08:08:00Z', nextBeforeId: 'p8' }) // 慢的翻页
    ctl.pages.push({ items: [post('p9', 9)], nextBefore: '2026-11-12T08:09:00Z', nextBeforeId: 'p9' }) // 重新加载的第一页
    const more = m.loadMore()
    const first = m.loadFirst()
    await Promise.all([more, first])
    expect(m.posts.map((p) => p.id)).toEqual(['p9'])
    expect(m.next?.beforeId).toBe('p9')
  })
})
