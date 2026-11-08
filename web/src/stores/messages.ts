import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, type MessageDto } from '@/api/client'
import { db, OUTBOX_FILE_LIMIT, type AttachmentMeta, type LocalMessage, type OutboxEntry } from '@/db'
import { isOnline } from '@/net/online'
import { isConnected, markRead, react, sendMessage, type MessageDeletedEvent, type ReactionsEvent } from '@/realtime/connection'
import { imageSize, kindOf, uploadFile, type UploadHandle } from '@/upload'
import { newUuid } from '@/utils/uuid'
import { useConversationsStore } from './conversations'
import { useSessionStore } from './session'

/** 按 seq 有序插入或替换（同 id / 同 clientMessageId 视为同一条） */
function upsert(list: LocalMessage[], m: LocalMessage): void {
  const idx = list.findIndex((x) => x.id === m.id || (x.clientMessageId && x.clientMessageId === m.clientMessageId))
  if (idx >= 0) {
    const old = list[idx]!
    if (old.localUrl && !m.localUrl && old.status !== 'sent' && m.status === 'sent') URL.revokeObjectURL(old.localUrl)
    list[idx] = m
  } else {
    list.push(m)
  }
  list.sort((a, b) => a.seq - b.seq)
}

/** 批量版：只排一次序，翻页 / 读缓存时不会 O(n² log n) */
function upsertMany(list: LocalMessage[], items: LocalMessage[]): void {
  if (items.length === 1) { upsert(list, items[0]!); return }
  const byId = new Map<string, number>()
  const byClient = new Map<string, number>()
  list.forEach((m, i) => { byId.set(m.id, i); if (m.clientMessageId) byClient.set(m.clientMessageId, i) })
  const appended: LocalMessage[] = []
  for (const m of items) {
    const idx = byId.get(m.id) ?? (m.clientMessageId ? byClient.get(m.clientMessageId) : undefined)
    if (idx !== undefined) {
      const old = list[idx]!
      if (old.localUrl && !m.localUrl && old.status !== 'sent' && m.status === 'sent') URL.revokeObjectURL(old.localUrl)
      list[idx] = m
    } else {
      const at = list.length + appended.length
      appended.push(m)
      byId.set(m.id, at)
      if (m.clientMessageId) byClient.set(m.clientMessageId, at)
    }
  }
  if (appended.length) {
    list.push(...appended)
    list.sort((a, b) => a.seq - b.seq)
  }
}

/** 会话列表里的一行摘要 */
export function previewOf(m: Pick<MessageDto, 'type' | 'body' | 'deleted'>): string {
  if (m.deleted) return '[已撤回]'
  switch (m.type) {
    case 'Image': return '[图片]'
    case 'Video': return '[视频]'
    case 'Audio': return '[语音]'
    case 'File': return '[文件]'
    case 'Sticker': return '[表情]'
    case 'Call': return '[通话]'
    default: return m.body ?? ''
  }
}

/** 写进 IndexedDB 的纯对象：去掉 Vue 代理与仅内存字段 */
function snapshot(m: LocalMessage): LocalMessage {
  const plain = JSON.parse(JSON.stringify(m)) as LocalMessage
  delete plain.localUrl
  delete plain.uploadProgress
  return plain
}

/** 一次发送尝试的结果：sent 已确认；retry 连接问题，稍后自动重发；rejected 服务器拒绝，标记失败等用户处理 */
export type SendOutcome = 'sent' | 'retry' | 'rejected'

/**
 * 发件箱排序键：严格递增。同一毫秒内连发（一次选多张图、快速回车）时 Date.now() 会相同，
 * 相同就会退回按随机主键排序，顺序错乱。
 */
let lastQueuedAt = 0
function nextQueuedAt(): number {
  lastQueuedAt = Math.max(Date.now(), lastQueuedAt + 1)
  return lastQueuedAt
}

/** 待上传附件的原文件（内存）；刷新后从发件箱恢复（≤ 50 MB 的才有） */
const pendingFiles = new Map<string, Blob>()
const uploads = new Map<string, UploadHandle>()
/** 用户主动取消的上传，失败回调里不要再标记失败 */
const cancelled = new Set<string>()
/**
 * IndexedDB 写不进去（空间满、Safari 丢连接）的发件箱条目：只在内存里，照样参与补发。
 * 发件箱不能成为发消息的单点故障。
 */
const memOutbox = new Map<string, OutboxEntry>()
/** 只在本页内存里的原文件（>50 MB 或没存进库）：用 Web Lock 声明"我还拿着"，别的标签页不要把它判成丢失 */
const fileLockReleasers = new Map<string, () => void>()

function fileLockName(clientMessageId: string): string {
  return `pc-file:${clientMessageId}`
}

function holdFileLock(clientMessageId: string): void {
  if (fileLockReleasers.has(clientMessageId) || typeof navigator === 'undefined' || !navigator.locks) return
  void navigator.locks
    .request(fileLockName(clientMessageId), () => new Promise<void>((resolve) => fileLockReleasers.set(clientMessageId, resolve)))
    .catch(() => undefined)
}

function releaseFileLock(clientMessageId: string): void {
  fileLockReleasers.get(clientMessageId)?.()
  fileLockReleasers.delete(clientMessageId)
}

/**
 * 上传失败怎么算：
 * - failed：本地文件读不了，或连着服务器时被明确拒绝（4xx/5xx，408/409/423/429 除外）→ 等用户重试 / 删除；
 * - retry：断网、服务器 / 隧道不通、请求超时等 → 保持排队，连上后自动续传（tus 按指纹续传）。
 */
export function classifyUploadError(err: unknown, online: boolean, connected: boolean): 'failed' | 'retry' {
  const name = (err as { name?: string } | null)?.name
  if (name === 'NotReadableError' || name === 'NotFoundError') return 'failed'
  let status = 0
  try {
    status = (err as { originalResponse?: { getStatus(): number } | null } | null)?.originalResponse?.getStatus() ?? 0
  } catch {
    status = 0
  }
  if (online && connected && status >= 400 && ![408, 409, 423, 429].includes(status)) return 'failed'
  return 'retry'
}

const PENDING_READS_KEY = 'pc.pendingReads'
function loadPendingReads(): Map<string, number> {
  try {
    const raw = localStorage.getItem(PENDING_READS_KEY)
    return new Map(Object.entries(raw ? (JSON.parse(raw) as Record<string, number>) : {}))
  } catch {
    return new Map()
  }
}
function savePendingReads(m: Map<string, number>): void {
  try {
    if (m.size) localStorage.setItem(PENDING_READS_KEY, JSON.stringify(Object.fromEntries(m)))
    else localStorage.removeItem(PENDING_READS_KEY)
  } catch {
    // 隐私模式：只是 App 被杀后会丢
  }
}

export const useMessagesStore = defineStore('messages', () => {
  /** conversationId → 按 seq 升序的消息；pending 消息 seq 为 Number.MAX_SAFE_INTEGER 排在最后 */
  const byConversation = ref<Record<string, LocalMessage[]>>({})
  /** blobId → 版本号；缩略图生成完成（MessageUpdated）后 +1，强制 img 重新请求 */
  const blobVersion = ref<Record<string, number>>({})
  /** 发件箱里还没被服务器确认的消息数（含失败的），连接横幅用 */
  const outboxCount = ref(0)
  /** 离线时读过的会话：连上后补发已读；存 localStorage，App 被系统杀掉也不丢 */
  const pendingReads = loadPendingReads()
  /** 上传因网络问题中断后，过一会儿再试一次（连着的话） */
  let retryTimer: ReturnType<typeof setTimeout> | undefined

  function list(conversationId: string): LocalMessage[] {
    return byConversation.value[conversationId] ?? (byConversation.value[conversationId] = [])
  }

  function findByClientId(conversationId: string, clientMessageId: string): LocalMessage | undefined {
    return list(conversationId).find((x) => x.clientMessageId === clientMessageId)
  }

  /** 每个会话本地已确认的最大 seq，供 Sync 使用 */
  function lastSeqs(): Record<string, number> {
    const result: Record<string, number> = {}
    for (const [cid, msgs] of Object.entries(byConversation.value)) {
      const confirmed = msgs.filter((m) => m.status !== 'pending' && m.status !== 'failed')
      if (confirmed.length) result[cid] = confirmed[confirmed.length - 1]!.seq
    }
    return result
  }

  /** 服务端是否还有更早的消息；向上翻到头后置 false */
  const hasMore = ref<Record<string, boolean>>({})
  const loadingOlder = ref<Record<string, boolean>>({})
  const PAGE = 50

  /** 先读 IndexedDB 秒开（含发件箱里未发出的），再拉最新一页；离线时只用本地 */
  async function load(conversationId: string, limit = PAGE): Promise<void> {
    const cached = await db.messages.where('[conversationId+seq]').between([conversationId, 0], [conversationId, Infinity]).toArray()
    if (cached.length) upsertMany(list(conversationId), cached)
    restoreEntries(await db.outbox.where('conversationId').equals(conversationId).toArray())
    try {
      const { data } = await api.GET('/api/conversations/{conversationId}/messages', {
        params: { path: { conversationId }, query: { limit } },
      })
      if (data) {
        await applyIncoming(data)
        if (data.length < limit) hasMore.value[conversationId] = false
        else if (!(conversationId in hasMore.value)) hasMore.value[conversationId] = true
      }
    } catch {
      // 离线 / 服务器不可达：沿用本地缓存，连上后 Sync 会补齐
    }
  }

  /** 向上翻页：取比本地最早一条还早的消息。返回拿到的条数 */
  async function loadOlder(conversationId: string, limit = PAGE): Promise<number> {
    if (hasMore.value[conversationId] === false || loadingOlder.value[conversationId]) return 0
    const first = list(conversationId).find((m) => m.status === 'sent')
    if (!first) return 0
    loadingOlder.value[conversationId] = true
    try {
      const { data } = await api.GET('/api/conversations/{conversationId}/messages', {
        params: { path: { conversationId }, query: { beforeSeq: first.seq, limit } },
      })
      if (!data) return 0
      await applyIncoming(data)
      if (data.length < limit) hasMore.value[conversationId] = false
      return data.length
    } catch {
      return 0
    } finally {
      loadingOlder.value[conversationId] = false
    }
  }

  /** 服务端来的消息（实时、Sync、分页、发送回执）统一走这里：替换 pending、写缓存、清发件箱、更新会话摘要 */
  async function applyIncoming(messages: MessageDto[], opts: { bumpBlobs?: boolean } = {}): Promise<void> {
    if (!messages.length) return
    const conversations = useConversationsStore()
    const me = useSessionStore().user?.id
    const byConv = new Map<string, LocalMessage[]>()
    const confirmedMine: string[] = []
    for (const m of messages) {
      const bucket = byConv.get(m.conversationId) ?? []
      bucket.push({ ...m, status: 'sent' })
      byConv.set(m.conversationId, bucket)
      conversations.bumpLastSeq(m.conversationId, m.seq, previewOf(m), m.createdAt)
      if (m.senderId === me) {
        confirmedMine.push(m.clientMessageId)
        pendingFiles.delete(m.clientMessageId)
        uploads.delete(m.clientMessageId)
        memOutbox.delete(m.clientMessageId)
        releaseFileLock(m.clientMessageId)
      }
      if (opts.bumpBlobs) {
        for (const a of m.attachments) blobVersion.value[a.blobId] = (blobVersion.value[a.blobId] ?? 0) + 1
      }
    }
    for (const [cid, bucket] of byConv) upsertMany(list(cid), bucket)
    // 本地缓存写失败不影响界面和收发（下次打开会话再从服务器拉）
    await db.messages.bulkPut(messages.map((m) => ({ ...m, status: 'sent' as const }))).catch(() => undefined)
    if (confirmedMine.length) {
      await db.outbox.bulkDelete(confirmedMine).catch(() => undefined)
      await refreshOutboxCount()
    }
  }

  async function refreshOutboxCount(): Promise<void> {
    try {
      const rows = await db.outbox.toCollection().primaryKeys()
      outboxCount.value = new Set<string>([...rows, ...memOutbox.keys()]).size
    } catch {
      outboxCount.value = memOutbox.size
    }
  }

  /** 发件箱 = 库里的条目 + 只在内存里的条目，按入队顺序 */
  async function readOutbox(): Promise<OutboxEntry[]> {
    const rows = await db.outbox.orderBy('queuedAt').toArray().catch(() => [] as OutboxEntry[])
    const seen = new Set(rows.map((r) => r.clientMessageId))
    for (const e of memOutbox.values()) if (!seen.has(e.clientMessageId)) rows.push(e)
    return rows.sort((a, b) => a.queuedAt - b.queuedAt)
  }

  function newPending(conversationId: string, partial: Partial<LocalMessage>): LocalMessage {
    const session = useSessionStore()
    const clientMessageId = newUuid()
    return {
      id: `local-${clientMessageId}`,
      conversationId,
      seq: Number.MAX_SAFE_INTEGER,
      senderId: session.user?.id ?? '',
      clientMessageId,
      type: 'Text',
      body: null,
      payload: null,
      replyToId: null,
      createdAt: new Date().toISOString(),
      editedAt: null,
      deleted: false,
      attachments: [],
      reactions: [],
      status: 'pending',
      ...partial,
    }
  }

  // ---------------- 发件箱 ----------------

  /** 先写发件箱再显示：即使马上断网、刷新、关掉 App，这条消息也不会丢 */
  async function enqueue(m: LocalMessage, file?: Blob, fileName?: string): Promise<void> {
    if (file) pendingFiles.set(m.clientMessageId, file)
    upsert(list(m.conversationId), m)
    const entry: OutboxEntry = {
      clientMessageId: m.clientMessageId,
      conversationId: m.conversationId,
      queuedAt: nextQueuedAt(),
      message: snapshot(m),
    }
    if (file && file.size <= OUTBOX_FILE_LIMIT) {
      entry.file = file
      entry.fileName = fileName
    }
    let stored = false
    try {
      await db.outbox.put(entry)
      stored = true
    } catch {
      // 配额不足等：先不带原文件再试一次（刷新后不能续传，但消息不丢）
      if (entry.file) {
        delete entry.file
        delete entry.fileName
        stored = await db.outbox.put(entry).then(() => true, () => false)
      }
    }
    // 库里写不进去就留在内存发件箱里，照样发
    if (!stored) memOutbox.set(entry.clientMessageId, entry)
    // 原文件只在本页内存里：声明持有，别的标签页不会把这条判成"文件丢失"
    if (file && !entry.file) holdFileLock(entry.clientMessageId)
    await refreshOutboxCount()
    void flushOutbox()
  }

  /** 把发件箱条目放回内存列表（刷新后 / 打开会话时）；已经在内存里的不覆盖，以免丢掉进度和预览 */
  function restoreEntries(entries: OutboxEntry[]): void {
    // 按入队顺序放回：按会话索引查出来的是主键（随机 UUID）顺序；pending 消息 seq 相同，靠稳定排序保持插入顺序
    const ordered = [...entries].sort((a, b) => a.queuedAt - b.queuedAt)
    for (const e of ordered) {
      const target = list(e.conversationId)
      if (target.some((x) => x.clientMessageId === e.clientMessageId)) continue
      const m: LocalMessage = { ...e.message }
      if (e.file) {
        if (!pendingFiles.has(e.clientMessageId)) pendingFiles.set(e.clientMessageId, e.file)
        const kind = m.attachments[0]?.kind
        if (kind && kind !== 'File') m.localUrl = URL.createObjectURL(e.file)
      }
      upsert(target, m)
    }
  }

  /** 启动时调用：恢复所有会话的发件箱，并在能发的时候补发 */
  async function restoreOutbox(): Promise<void> {
    restoreEntries(await readOutbox())
    await refreshOutboxCount()
    void flushOutbox()
  }

  async function persist(m: LocalMessage): Promise<void> {
    const mem = memOutbox.get(m.clientMessageId)
    if (mem) mem.message = snapshot(m)
    await db.outbox.update(m.clientMessageId, { message: snapshot(m) }).catch(() => undefined)
  }

  function markFailed(m: LocalMessage): void {
    m.status = 'failed'
    m.uploadProgress = undefined
    void persist(m)
  }

  async function trySend(m: LocalMessage): Promise<SendOutcome> {
    if (!isConnected()) return 'retry'
    const att = m.attachments[0]
    let dto: MessageDto
    try {
      dto = await sendMessage({
        conversationId: m.conversationId,
        clientMessageId: m.clientMessageId,
        type: m.type,
        body: m.body,
        attachments: att ? [{ blobId: att.blobId, kind: att.kind, meta: att.meta }] : undefined,
      })
    } catch {
      // 调用中途断线：连接已不在 Connected，下次连上再发；仍连着说明是服务器拒绝（无权限、参数错误等）
      return isConnected() && isOnline() ? 'rejected' : 'retry'
    }
    // 服务器已经收下了：之后本地写库出错也不能把它算成发送失败
    await applyIncoming([dto]).catch(() => undefined)
    return 'sent'
  }

  function scheduleRetry(): void {
    if (retryTimer) return
    retryTimer = setTimeout(() => {
      retryTimer = undefined
      if (isConnected()) void flushOutbox()
    }, 15_000)
  }

  /** 附件上传与消息发送解耦：上传可以并行，拿到 blobId 后再按顺序发 */
  function startUpload(m: LocalMessage): void {
    const id = m.clientMessageId
    if (uploads.has(id)) return
    const file = pendingFiles.get(id)
    if (!file) {
      // 原文件不在本页：可能是另一个标签页正拿着（>50 MB 只存内存），那就不管它；
      // 没人拿着才说明刷新前没存下来，只能让用户重新选择
      if (typeof navigator !== 'undefined' && navigator.locks) {
        void navigator.locks
          .request(fileLockName(id), { ifAvailable: true }, async (lock) => {
            if (lock) markFailed(m)
          })
          .catch(() => undefined)
      } else {
        markFailed(m)
      }
      return
    }
    // 服务器连不上时不开始（会白白重试后失败），连上后 flushOutbox 会再来
    if (!isOnline() || !isConnected()) return
    cancelled.delete(id)
    const asFile = file instanceof File ? file : new File([file], parseFileName(m), { type: file.type })
    const handle = uploadFile(asFile, { onProgress: (r) => { m.uploadProgress = r } })
    uploads.set(id, handle)
    handle.done.then(
      async (blobId) => {
        uploads.delete(id)
        const att = m.attachments[0]
        if (!att || cancelled.has(id)) return
        att.blobId = blobId
        m.uploadProgress = 1
        await persist(m).catch(() => undefined)
        void flushOutbox()
      },
      (err: unknown) => {
        uploads.delete(id)
        if (cancelled.has(id)) return
        m.uploadProgress = undefined
        if (classifyUploadError(err, isOnline(), isConnected()) === 'failed') markFailed(m)
        // 网络问题：保持排队；连上 / 联网时会重新开始，连着的话 15 秒后再试一次
        else scheduleRetry()
      },
    )
  }

  function parseFileName(m: LocalMessage): string {
    try {
      return (JSON.parse(m.attachments[0]?.meta ?? '{}') as AttachmentMeta).name ?? 'file'
    } catch {
      return 'file'
    }
  }

  let flushing = false
  let flushAgain = false

  /**
   * 补发发件箱：
   * 1. 所有还没上传完的附件先开始上传（并行，不阻塞文字）；
   * 2. 能发的按入队顺序逐条发，遇到连接问题就停，下次连上 / 上传完成再继续。
   * 失败（服务器拒绝）的条目跳过，等用户点重试。
   */
  async function flushOutbox(): Promise<void> {
    if (flushing) { flushAgain = true; return }
    flushing = true
    try {
      do {
        flushAgain = false
        const entries = await readOutbox()
        restoreEntries(entries)
        const live = entries
          .map((e) => findByClientId(e.conversationId, e.clientMessageId))
          .filter((m): m is LocalMessage => !!m && m.status !== 'failed' && m.status !== 'sent')
        for (const m of live) {
          const att = m.attachments[0]
          if (att && !att.blobId) startUpload(m)
        }
        for (const m of live) {
          const att = m.attachments[0]
          if (att && !att.blobId) continue
          // 循环里有 await：期间被取消 / 已确认 / 已标失败的跳过
          if (cancelled.has(m.clientMessageId) || m.status !== 'pending') continue
          const outcome = await trySend(m)
          if (outcome === 'retry') break
          if (outcome === 'rejected') markFailed(m)
        }
      } while (flushAgain)
    } finally {
      flushing = false
    }
  }

  // ---------------- 发送入口 ----------------

  /** 乐观发送：先进发件箱并显示，连着就立刻发，没连上就排队 */
  async function sendText(conversationId: string, body: string): Promise<void> {
    await enqueue(newPending(conversationId, { type: 'Text', body }))
  }

  /** 每个文件一条消息：先显示本地预览与进度，上传完成后再发 */
  async function sendFiles(conversationId: string, files: File[]): Promise<void> {
    await Promise.all(files.map((f) => sendFile(conversationId, f)))
  }

  /** 语音：带时长，气泡宽度与进度条都靠它 */
  async function sendVoice(conversationId: string, file: File, duration: number): Promise<void> {
    await sendFile(conversationId, file, { duration })
  }

  async function sendFile(conversationId: string, file: File, extra: Partial<AttachmentMeta> = {}): Promise<void> {
    const kind = kindOf(file)
    const size = kind === 'Image' ? await imageSize(file) : null
    const meta = JSON.stringify({ name: file.name, size: file.size, mime: file.type || 'application/octet-stream', ...(size ?? {}), ...extra })
    const m = newPending(conversationId, {
      type: kind,
      attachments: [{ id: '', blobId: '', kind, meta }],
      uploadProgress: 0,
      localUrl: kind === 'File' ? undefined : URL.createObjectURL(file),
    })
    await enqueue(m, file, file.name)
  }

  /** 重试失败的消息。返回 false 表示原文件已丢失（刷新前没能保存），需要重新选择 */
  async function retry(conversationId: string, clientMessageId: string): Promise<boolean> {
    const m = findByClientId(conversationId, clientMessageId)
    if (!m) return true
    const att = m.attachments[0]
    if (att && !att.blobId && !pendingFiles.has(clientMessageId)) return false
    m.status = 'pending'
    await persist(m).catch(() => undefined)
    void flushOutbox()
    return true
  }

  function cancel(conversationId: string, clientMessageId: string): void {
    cancelled.add(clientMessageId)
    void uploads.get(clientMessageId)?.abort()
    uploads.delete(clientMessageId)
    pendingFiles.delete(clientMessageId)
    memOutbox.delete(clientMessageId)
    releaseFileLock(clientMessageId)
    void db.outbox.delete(clientMessageId).catch(() => undefined).then(refreshOutboxCount)
    const msgs = list(conversationId)
    const idx = msgs.findIndex((x) => x.clientMessageId === clientMessageId)
    if (idx >= 0) {
      const [m] = msgs.splice(idx, 1)
      if (m?.localUrl) URL.revokeObjectURL(m.localUrl)
    }
  }

  // ---------------- 已读 / 回应 / 撤回 ----------------

  async function read(conversationId: string): Promise<void> {
    const msgs = list(conversationId).filter((m) => m.status === 'sent')
    const last = msgs[msgs.length - 1]
    if (!last) return
    const conversations = useConversationsStore()
    const c = conversations.byId(conversationId)
    if (c && last.seq > c.lastReadSeq) {
      c.lastReadSeq = last.seq
      conversations.touch(c.id)
      if (!(await markRead(conversationId, last.seq))) {
        pendingReads.set(conversationId, last.seq)
        savePendingReads(pendingReads)
      }
    }
  }

  async function flushPendingReads(): Promise<void> {
    for (const [cid, seq] of [...pendingReads]) {
      if (!(await markRead(cid, seq))) return
      // 期间又读到更新的位置就保留新的
      if (pendingReads.get(cid) === seq) pendingReads.delete(cid)
      savePendingReads(pendingReads)
    }
  }

  function find(conversationId: string, messageId: string): LocalMessage | undefined {
    return list(conversationId).find((m) => m.id === messageId)
  }

  /** 乐观切换：先改本地，再发 Hub；失败（含离线）回滚。服务端的 ReactionChanged 会再校正一次 */
  async function toggleReaction(conversationId: string, messageId: string, emoji: string): Promise<boolean> {
    const m = find(conversationId, messageId)
    const me = useSessionStore().user?.id ?? ''
    if (!m || !me) return false
    const before = m.reactions
    const idx = before.findIndex((r) => r.userId === me && r.emoji === emoji)
    m.reactions = idx >= 0 ? before.filter((_, i) => i !== idx) : [...before, { userId: me, emoji }]
    try {
      await react(messageId, emoji)
      return true
    } catch {
      m.reactions = before
      return false
    }
  }

  async function applyReactions(e: ReactionsEvent): Promise<void> {
    const m = find(e.conversationId, e.messageId)
    if (!m) return
    m.reactions = e.reactions
    await db.messages.update(m.id, { reactions: e.reactions })
  }

  async function markDeleted(e: MessageDeletedEvent): Promise<void> {
    const m = find(e.conversationId, e.messageId)
    if (!m) return
    m.deleted = true
    m.body = null
    m.payload = null
    m.attachments = []
    await db.messages.update(m.id, { deleted: true, body: null, payload: null, attachments: [] })
    const conversations = useConversationsStore()
    const c = conversations.byId(e.conversationId)
    if (c && c.lastSeq === e.seq) {
      c.lastMessagePreview = previewOf(m)
      conversations.touch(c.id)
    }
  }

  /** 撤回：服务端会向会话广播 MessageDeleted（含自己）。离线 / 超时 / 无权限返回 false */
  async function recall(conversationId: string, messageId: string): Promise<boolean> {
    try {
      const { response } = await api.DELETE('/api/messages/{id}', { params: { path: { id: messageId } } })
      if (response.ok) await markDeleted({ conversationId, messageId, seq: find(conversationId, messageId)?.seq ?? 0 })
      return response.ok
    } catch {
      return false
    }
  }

  return {
    byConversation, blobVersion, hasMore, loadingOlder, outboxCount,
    list, lastSeqs, load, loadOlder, applyIncoming,
    sendText, sendFiles, sendVoice, retry, cancel, restoreOutbox, flushOutbox,
    read, flushPendingReads, toggleReaction, applyReactions, markDeleted, recall,
  }
})
