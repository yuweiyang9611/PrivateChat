// 发件箱（离线发送排队）的行为测试：真实的 Pinia store + Dexie（fake-indexeddb），SignalR / 上传 / API 用可控的假实现。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory, IDBKeyRange } from 'fake-indexeddb'
import Dexie from 'dexie'

const ME = '00000000-0000-7000-8000-00000000000a'
const CID = '00000000-0000-7000-8000-0000000000c1'

interface SentDto { conversationId: string; clientMessageId: string; type: string; body?: string | null; attachments?: { blobId: string; kind: string; meta: string | null }[] }
interface PendingUpload { file: File; resolve: (blobId: string) => void; reject: (e: unknown) => void }

const ctl = vi.hoisted(() => ({
  connected: false,
  online: true,
  seq: 100,
  sent: [] as SentDto[],
  /** 返回 'drop' 表示调用中途断线，'reject' 表示服务器拒绝，否则正常回执 */
  sendBehaviour: [] as ('ok' | 'drop' | 'reject')[],
  markReadCalls: [] as [string, number][],
  uploads: [] as PendingUpload[],
  /** 非空时 GET /api/conversations 返回它，否则抛"离线" */
  serverConversations: null as null | unknown[],
  conversationGets: 0,
}))

function echo(dto: SentDto) {
  return {
    id: `srv-${dto.clientMessageId}`,
    conversationId: dto.conversationId,
    seq: ++ctl.seq,
    senderId: ME,
    clientMessageId: dto.clientMessageId,
    type: dto.type,
    body: dto.body ?? null,
    payload: null,
    replyToId: null,
    createdAt: new Date().toISOString(),
    editedAt: null,
    deleted: false,
    attachments: (dto.attachments ?? []).map((a, i) => ({ id: `att-${i}`, ...a })),
    reactions: [],
  }
}

vi.mock('@/realtime/connection', async () => {
  const { ref } = await import('vue')
  return {
    connectionState: ref(0),
    isConnected: () => ctl.connected,
    startConnection: async () => undefined,
    stopConnection: async () => undefined,
    react: async () => ({}),
    markRead: async (cid: string, seq: number) => {
      ctl.markReadCalls.push([cid, seq])
      return ctl.connected
    },
    sendMessage: async (dto: SentDto) => {
      ctl.sent.push(JSON.parse(JSON.stringify(dto)))
      const b = ctl.sendBehaviour.shift() ?? 'ok'
      if (b === 'drop') {
        ctl.connected = false
        throw new Error('Invocation canceled due to the underlying connection being closed.')
      }
      if (b === 'reject') throw new Error("An unexpected error occurred invoking 'SendMessage' on the server.")
      return echo(dto)
    },
  }
})

vi.mock('@/api/client', () => ({
  api: {
    GET: async (path: string) => {
      if (path === '/api/conversations' && ctl.serverConversations) {
        ctl.conversationGets++
        return { data: ctl.serverConversations }
      }
      throw new TypeError('Failed to fetch')
    },
    POST: async () => { throw new TypeError('Failed to fetch') },
    DELETE: async () => { throw new TypeError('Failed to fetch') },
  },
  fetchMe: async () => ({ probe: 'offline' }),
  reauthenticate: () => false,
  setUnauthorizedHandler: () => undefined,
  handleAuthLoss: () => undefined,
}))

vi.mock('@/push', () => ({ disablePush: async () => undefined }))

vi.mock('@/upload', () => ({
  kindOf: (f: File) => (f.type.startsWith('image/') ? 'Image' : f.type.startsWith('audio/') ? 'Audio' : 'File'),
  imageSize: async () => ({ width: 40, height: 30 }),
  uploadFile: (file: File) => {
    let resolve!: (id: string) => void
    let reject!: (e: unknown) => void
    const done = new Promise<string>((res, rej) => { resolve = res; reject = rej })
    ctl.uploads.push({ file, resolve, reject })
    return { done, abort: async () => reject(new Error('aborted')) }
  },
}))

/** 模拟一次"打开页面"：模块、Pinia 全新，IndexedDB 保留（除非 beforeEach 清掉） */
async function boot() {
  vi.resetModules()
  const { createPinia, setActivePinia } = await import('pinia')
  setActivePinia(createPinia())
  const { useSessionStore } = await import('@/stores/session')
  const { useMessagesStore } = await import('@/stores/messages')
  const { db } = await import('@/db')
  useSessionStore().user = {
    id: ME, email: 'me@example.com', username: 'me', displayName: '我', role: 'Member',
    elderMode: false, avatarBlobId: null, authScheme: 'Local',
  }
  return { store: useMessagesStore(), db }
}

const settle = () => new Promise((r) => setTimeout(r, 20))

beforeEach(() => {
  // Dexie 在 node_modules 里不随 resetModules 重新加载，要直接换它记住的 IndexedDB 工厂
  const factory = new IDBFactory()
  globalThis.indexedDB = factory
  Dexie.dependencies.indexedDB = factory
  Dexie.dependencies.IDBKeyRange = IDBKeyRange
  Object.assign(ctl, { connected: false, online: true, seq: 100, sent: [], sendBehaviour: [], markReadCalls: [], uploads: [], serverConversations: null, conversationGets: 0 })
  localStorage.clear()
  vi.spyOn(navigator, 'onLine', 'get').mockImplementation(() => ctl.online)
  let n = 0
  URL.createObjectURL = vi.fn(() => `blob:test/${++n}`)
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('发件箱：文字消息', () => {
  it('没连上时排队并写入 IndexedDB，连上后补发并清出发件箱', async () => {
    const { store, db } = await boot()
    await store.sendText(CID, '晚上回来吃饭')
    await settle()

    const pending = store.list(CID)
    expect(pending).toHaveLength(1)
    expect(pending[0]!.status).toBe('pending')
    expect(ctl.sent).toHaveLength(0)
    expect(await db.outbox.count()).toBe(1)
    expect(store.outboxCount).toBe(1)

    ctl.connected = true
    await store.flushOutbox()
    await settle()

    expect(ctl.sent).toHaveLength(1)
    expect(ctl.sent[0]).toMatchObject({ conversationId: CID, type: 'Text', body: '晚上回来吃饭' })
    const [m] = store.list(CID)
    expect(m!.status).toBe('sent')
    expect(m!.seq).toBe(101)
    expect(await db.outbox.count()).toBe(0)
    expect(store.outboxCount).toBe(0)
    expect(await db.messages.count()).toBe(1)
  })

  it('多条排队的消息按发送顺序补发', async () => {
    const { store } = await boot()
    for (const body of ['一', '二', '三']) await store.sendText(CID, body)
    ctl.connected = true
    await store.flushOutbox()
    await settle()
    expect(ctl.sent.map((d) => d.body)).toEqual(['一', '二', '三'])
    expect(store.list(CID).map((m) => [m.body, m.seq])).toEqual([['一', 101], ['二', 102], ['三', 103]])
  })

  it('发送途中断线：保持排队、停止后续发送，重连后从断点继续且不重复', async () => {
    const { store } = await boot()
    ctl.connected = false
    for (const body of ['一', '二', '三']) await store.sendText(CID, body)
    ctl.connected = true
    ctl.sendBehaviour = ['ok', 'drop']
    await store.flushOutbox()
    await settle()
    expect(ctl.sent.map((d) => d.body)).toEqual(['一', '二'])
    expect(store.list(CID).map((m) => m.status)).toEqual(['sent', 'pending', 'pending'])

    ctl.connected = true
    await store.flushOutbox()
    await settle()
    // "二" 重发一次（服务器按 clientMessageId 幂等），"三" 紧随其后
    expect(ctl.sent.map((d) => d.body)).toEqual(['一', '二', '二', '三'])
    expect(ctl.sent[1]!.clientMessageId).toBe(ctl.sent[2]!.clientMessageId)
    expect(store.list(CID).every((m) => m.status === 'sent')).toBe(true)
  })

  it('服务器拒绝：标记失败、不自动重发，后面的照常发；点重试才再发', async () => {
    const { store, db } = await boot()
    ctl.connected = true
    ctl.sendBehaviour = ['reject']
    await store.sendText(CID, '坏的')
    await settle()
    await store.sendText(CID, '好的')
    await settle()

    const bad = store.list(CID).find((m) => m.body === '坏的')!
    expect(bad.status).toBe('failed')
    expect(store.list(CID).find((m) => m.body === '好的')!.status).toBe('sent')
    expect((await db.outbox.get(bad.clientMessageId))!.message.status).toBe('failed')

    await store.flushOutbox()
    await settle()
    expect(ctl.sent.filter((d) => d.body === '坏的')).toHaveLength(1)

    expect(await store.retry(CID, bad.clientMessageId)).toBe(true)
    await settle()
    expect(ctl.sent.filter((d) => d.body === '坏的')).toHaveLength(2)
    expect(store.list(CID).find((m) => m.body === '坏的')!.status).toBe('sent')
  })

  it('刷新页面后发件箱恢复显示，连上后补发', async () => {
    const first = await boot()
    await first.store.sendText(CID, '刷新前写的')
    await settle()

    const second = await boot()
    await second.store.restoreOutbox()
    const restored = second.store.list(CID)
    expect(restored).toHaveLength(1)
    expect(restored[0]!.body).toBe('刷新前写的')
    expect(restored[0]!.status).toBe('pending')

    ctl.connected = true
    await second.store.flushOutbox()
    await settle()
    expect(ctl.sent.map((d) => d.body)).toEqual(['刷新前写的'])
    expect(await second.db.outbox.count()).toBe(0)
  })

  it('打开会话时把发件箱里的消息放回列表，且排在已发消息之后', async () => {
    const first = await boot()
    ctl.connected = true
    await first.store.sendText(CID, '已发')
    await settle()
    ctl.connected = false
    await first.store.sendText(CID, '未发')
    await settle()

    const second = await boot()
    await second.store.load(CID) // api.GET 抛错 = 离线，只用本地
    expect(second.store.list(CID).map((m) => [m.body, m.status])).toEqual([['已发', 'sent'], ['未发', 'pending']])
  })

  it('打开会话恢复多条排队消息时保持写入顺序（不按随机主键排）', async () => {
    const first = await boot()
    const bodies = Array.from({ length: 8 }, (_, i) => `第${i + 1}条`)
    for (const b of bodies) await first.store.sendText(CID, b)
    await settle()

    const second = await boot()
    await second.store.load(CID)
    expect(second.store.list(CID).map((m) => m.body)).toEqual(bodies)
  })

  it('广播先于调用返回到达时，发件箱同样被清掉', async () => {
    const { store, db } = await boot()
    await store.sendText(CID, '先到的广播')
    await settle()
    const entry = (await db.outbox.toArray())[0]!
    await store.applyIncoming([echo({ conversationId: CID, clientMessageId: entry.clientMessageId, type: 'Text', body: '先到的广播' }) as never])
    expect(await db.outbox.count()).toBe(0)
    expect(store.list(CID)).toHaveLength(1)
    expect(store.list(CID)[0]!.status).toBe('sent')
  })

  it('删除失败的消息会同时清出发件箱', async () => {
    const { store, db } = await boot()
    ctl.connected = true
    ctl.sendBehaviour = ['reject']
    await store.sendText(CID, '不要了')
    await settle()
    const m = store.list(CID)[0]!
    store.cancel(CID, m.clientMessageId)
    await settle()
    expect(store.list(CID)).toHaveLength(0)
    expect(await db.outbox.count()).toBe(0)
  })
})

describe('发件箱：附件', () => {
  it('离线时不开始上传；联网后上传，拿到 blobId 再发消息', async () => {
    const { store } = await boot()
    ctl.online = false
    await store.sendFiles(CID, [new File(['x'.repeat(10)], 'a.jpg', { type: 'image/jpeg' })])
    await settle()
    expect(ctl.uploads).toHaveLength(0)
    expect(store.list(CID)[0]!.status).toBe('pending')

    ctl.online = true
    ctl.connected = true
    await store.flushOutbox()
    expect(ctl.uploads).toHaveLength(1)
    expect(ctl.sent).toHaveLength(0)

    ctl.uploads[0]!.resolve('blob-1')
    await vi.waitFor(() => expect(ctl.sent).toHaveLength(1))
    expect(ctl.sent[0]).toMatchObject({ type: 'Image', attachments: [{ blobId: 'blob-1', kind: 'Image' }] })
    await settle()
    expect(store.list(CID)[0]!.status).toBe('sent')
  })

  it('附件上传中，后发的文字不被阻塞', async () => {
    const { store } = await boot()
    ctl.connected = true
    await store.sendFiles(CID, [new File(['x'], 'b.jpg', { type: 'image/jpeg' })])
    await store.sendText(CID, '文字先到')
    await settle()
    expect(ctl.sent.map((d) => d.body)).toEqual(['文字先到'])
    expect(ctl.uploads).toHaveLength(1)
  })

  it('已上传但发送被拒：重试时原文件还在就重新上传（blob 可能已被回收），拿到新 blobId 再发', async () => {
    const { store, db } = await boot()
    ctl.connected = true
    ctl.sendBehaviour = ['reject']
    await store.sendFiles(CID, [new File(['x'], 'd.jpg', { type: 'image/jpeg' })])
    await settle()
    ctl.uploads[0]!.resolve('blob-old')
    await vi.waitFor(() => expect(store.list(CID)[0]!.status).toBe('failed'))

    const m = store.list(CID)[0]!
    expect(await store.retry(CID, m.clientMessageId)).toBe(true)
    await settle()
    expect(ctl.uploads).toHaveLength(2)
    expect((await db.outbox.get(m.clientMessageId))!.message.attachments[0]!.blobId).toBe('')

    ctl.uploads[1]!.resolve('blob-new')
    await vi.waitFor(() => expect(ctl.sent).toHaveLength(2))
    expect(ctl.sent[1]).toMatchObject({ attachments: [{ blobId: 'blob-new' }] })
    await settle()
    expect(store.list(CID)[0]!.status).toBe('sent')
  })

  it('网络类上传失败继续排队；连着服务器时被明确拒绝才算失败', async () => {
    const { store } = await boot()
    ctl.connected = true
    await store.sendFiles(CID, [new File(['x'], 'c.jpg', { type: 'image/jpeg' })])
    await settle()

    // 断网
    ctl.online = false
    ctl.uploads[0]!.reject(new Error('network'))
    await settle()
    expect(store.list(CID)[0]!.status).toBe('pending')

    // 联网、连着，但请求没拿到响应（超时 / 隧道断）：仍是网络问题
    ctl.online = true
    await store.flushOutbox()
    expect(ctl.uploads).toHaveLength(2)
    ctl.uploads[1]!.reject(Object.assign(new Error('tus: failed to upload chunk'), { originalResponse: null }))
    await settle()
    expect(store.list(CID)[0]!.status).toBe('pending')

    // 连着服务器且服务器回了 413：明确拒绝
    await store.flushOutbox()
    expect(ctl.uploads).toHaveLength(3)
    ctl.uploads[2]!.reject(Object.assign(new Error('tus: unexpected response'), { originalResponse: { getStatus: () => 413 } }))
    await settle()
    expect(store.list(CID)[0]!.status).toBe('failed')
  })

  it('浏览器在线但服务器连不上时不开始上传（避免白白重试后判失败），连上后才传', async () => {
    const { store } = await boot()
    ctl.online = true
    ctl.connected = false
    await store.sendFiles(CID, [new File(['x'], 'e.jpg', { type: 'image/jpeg' })])
    await settle()
    expect(ctl.uploads).toHaveLength(0)
    expect(store.list(CID)[0]!.status).toBe('pending')

    ctl.connected = true
    await store.flushOutbox()
    expect(ctl.uploads).toHaveLength(1)
  })

  it('取消上传：中止上传、移出列表与发件箱，之后不会再发', async () => {
    const { store, db } = await boot()
    ctl.connected = true
    await store.sendFiles(CID, [new File(['x'], 'd.jpg', { type: 'image/jpeg' })])
    await settle()
    const m = store.list(CID)[0]!
    store.cancel(CID, m.clientMessageId)
    await settle()
    expect(store.list(CID)).toHaveLength(0)
    expect(await db.outbox.count()).toBe(0)
    await store.flushOutbox()
    await settle()
    expect(ctl.sent).toHaveLength(0)
  })
})

describe('离线已读', () => {
  it('没连上时记下，连上后补发', async () => {
    const { store } = await boot()
    const { useConversationsStore } = await import('@/stores/conversations')
    const conversations = useConversationsStore()
    conversations.items = [{ id: CID, type: 'Group', title: '全家群', avatarBlobId: null, lastSeq: 5, lastReadSeq: 0, unread: 5, mutedUntil: null, memberIds: [ME] }]
    await store.applyIncoming([{ ...echo({ conversationId: CID, clientMessageId: 'x', type: 'Text', body: 'hi' }), senderId: 'someone-else', seq: 5 } as never])

    await store.read(CID)
    expect(conversations.items[0]!.lastReadSeq).toBe(5)
    expect(ctl.markReadCalls).toEqual([[CID, 5]])

    ctl.connected = true
    await store.flushPendingReads()
    expect(ctl.markReadCalls).toEqual([[CID, 5], [CID, 5]])
    await store.flushPendingReads()
    expect(ctl.markReadCalls).toHaveLength(2)
  })
})

describe('发件箱：容错', () => {
  it('IndexedDB 写不进去时消息留在内存发件箱里，照样发出', async () => {
    const { store, db } = await boot()
    vi.spyOn(db.outbox, 'put').mockRejectedValue(new DOMException('quota', 'QuotaExceededError'))
    await store.sendText(CID, '库满了也要发')
    await settle()
    expect(store.list(CID)[0]!.status).toBe('pending')
    expect(store.outboxCount).toBe(1)

    ctl.connected = true
    await store.flushOutbox()
    await settle()
    expect(ctl.sent.map((d) => d.body)).toEqual(['库满了也要发'])
    expect(store.list(CID)[0]!.status).toBe('sent')
    expect(store.outboxCount).toBe(0)
  })

  it('服务器已收下但本地写库失败：仍算发送成功，不标失败', async () => {
    const { store, db } = await boot()
    vi.spyOn(db.messages, 'bulkPut').mockRejectedValue(new Error('IDB connection lost'))
    ctl.connected = true
    await store.sendText(CID, '写库失败')
    await settle()
    expect(ctl.sent).toHaveLength(1)
    expect(store.list(CID)[0]!.status).toBe('sent')
  })

  it('离线读过的会话记在 localStorage，App 被杀后重开仍会补发已读', async () => {
    const first = await boot()
    const { useConversationsStore } = await import('@/stores/conversations')
    useConversationsStore().items = [{ id: CID, type: 'Group', title: '全家群', avatarBlobId: null, lastSeq: 7, lastReadSeq: 0, unread: 7, mutedUntil: null, memberIds: [ME] }]
    await first.store.applyIncoming([{ ...echo({ conversationId: CID, clientMessageId: 'y', type: 'Text', body: 'hi' }), senderId: 'someone-else', seq: 7 } as never])
    await first.store.read(CID)
    expect(JSON.parse(localStorage.getItem('pc.pendingReads')!)).toEqual({ [CID]: 7 })

    const second = await boot()
    ctl.connected = true
    ctl.markReadCalls = []
    await second.store.flushPendingReads()
    expect(ctl.markReadCalls).toEqual([[CID, 7]])
    expect(localStorage.getItem('pc.pendingReads')).toBeNull()
  })

  it('收到不认识的会话的消息时刷新会话列表（离线期间被拉进的新群）', async () => {
    const { store } = await boot()
    const { useConversationsStore } = await import('@/stores/conversations')
    const conversations = useConversationsStore()
    ctl.serverConversations = [{ id: CID, type: 'Group', title: '全家群', avatarBlobId: null, lastSeq: 0, lastReadSeq: 0, unread: 0, mutedUntil: null, memberIds: [ME] }]
    await conversations.load()
    expect(ctl.conversationGets).toBe(1)

    const NEW = '00000000-0000-7000-8000-0000000000c2'
    ctl.serverConversations = [...ctl.serverConversations, { id: NEW, type: 'Group', title: '新群', avatarBlobId: null, lastSeq: 1, lastReadSeq: 0, unread: 1, mutedUntil: null, memberIds: [ME] }]
    await store.applyIncoming([{ ...echo({ conversationId: NEW, clientMessageId: 'z', type: 'Text', body: '欢迎' }), senderId: 'someone-else', seq: 1 } as never])
    await vi.waitFor(() => expect(ctl.conversationGets).toBe(2), { timeout: 2000 })
    expect(conversations.byId(NEW)?.title).toBe('新群')
  })

  it('原文件只在另一个标签页内存里时不判"文件丢失"；那个页面关掉后才判', async () => {
    // 极简 Web Locks：同一全局对象模拟同一浏览器的多个标签页
    const held = new Map<string, () => void>()
    const locks = {
      async request(name: string, a: unknown, b?: unknown) {
        const opts = (typeof a === 'function' ? {} : a) as { ifAvailable?: boolean }
        const cb = (typeof a === 'function' ? a : b) as (lock: unknown) => Promise<unknown>
        if (held.has(name)) {
          if (opts.ifAvailable) return cb(null)
          throw new Error('unexpected wait in test')
        }
        // 关闭标签页 = 浏览器强制释放它持有的锁
        held.set(name, () => held.delete(name))
        try {
          return await cb({ name })
        } finally {
          held.delete(name)
        }
      },
    }
    Object.defineProperty(navigator, 'locks', { value: locks, configurable: true })
    try {
      // 标签页 A：带原文件写库失败，只存了不带文件的条目 → 原文件只在 A 的内存里
      const a = await boot()
      const realPut = a.db.outbox.put.bind(a.db.outbox)
      type PutFn = typeof a.db.outbox.put
      const failWithFile: PutFn = ((entry: Parameters<PutFn>[0], key?: Parameters<PutFn>[1]) =>
        (entry as { file?: Blob }).file ? Promise.reject(new Error('quota')) : realPut(entry, key)) as PutFn
      vi.spyOn(a.db.outbox, 'put').mockImplementation(failWithFile)
      await a.store.sendFiles(CID, [new File(['x'], 'big.mov', { type: 'video/quicktime' })])
      await settle()
      vi.restoreAllMocks()
      vi.spyOn(navigator, 'onLine', 'get').mockImplementation(() => ctl.online)
      expect(held.size).toBe(1)

      // 标签页 B：恢复发件箱、连着服务器，但拿不到原文件 → 因为 A 还拿着，不判失败
      const b = await boot()
      ctl.connected = true
      await b.store.restoreOutbox()
      await settle()
      expect(b.store.list(CID)[0]!.status).toBe('pending')

      // A 关掉（释放锁）后，B 再补发时才判定原文件丢失
      for (const release of [...held.values()]) release()
      await settle()
      await b.store.flushOutbox()
      await settle()
      expect(b.store.list(CID)[0]!.status).toBe('failed')
    } finally {
      delete (navigator as { locks?: unknown }).locks
    }
  })
})

describe('MessageUpdated（缩略图 / 转码完成）', () => {
  it('只刷新已有的消息：不认识的旧消息不插进列表、不写进缓存，但 blob 版本照样 +1', async () => {
    const { store, db } = await boot()
    const old = { ...echo({ conversationId: CID, clientMessageId: 'old', type: 'Image', attachments: [{ blobId: 'b-old', kind: 'Image', meta: null }] }), senderId: 'someone-else', seq: 3 }
    await store.applyUpdated(old as never)
    expect(store.list(CID)).toHaveLength(0)
    expect(await db.messages.get(old.id)).toBeUndefined()
    expect(store.blobVersion['b-old']).toBe(1)
  })

  it('已有的消息原地替换，缓存里的也更新', async () => {
    const { store, db } = await boot()
    const m = { ...echo({ conversationId: CID, clientMessageId: 'm1', type: 'Video', attachments: [{ blobId: 'b-v', kind: 'Video', meta: '{"name":"a.mp4"}' }] }), senderId: 'someone-else' }
    await store.applyIncoming([m as never])
    const updated = { ...m, attachments: [{ ...m.attachments[0]!, meta: '{"name":"a.mp4","duration":3}' }] }
    await store.applyUpdated(updated as never)
    expect(store.list(CID)).toHaveLength(1)
    expect(store.list(CID)[0]!.attachments[0]!.meta).toContain('duration')
    expect((await db.messages.get(m.id))!.attachments[0]!.meta).toContain('duration')
    expect((await db.messages.get(m.id))!.status).toBe('sent')
    expect(store.blobVersion['b-v']).toBe(1)
  })
})
