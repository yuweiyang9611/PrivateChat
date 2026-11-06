import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, type MessageDto } from '@/api/client'
import { db, type LocalMessage } from '@/db'
import { markRead, sendMessage } from '@/realtime/connection'
import { imageSize, kindOf, uploadFile, type UploadHandle } from '@/upload'
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

/** 待发送附件的 File 对象只存内存，刷新后丢失（tus 指纹仍可续传，但需要用户重新选择文件） */
const pendingFiles = new Map<string, File>()
const uploads = new Map<string, UploadHandle>()

export const useMessagesStore = defineStore('messages', () => {
  /** conversationId → 按 seq 升序的消息；pending 消息 seq 为 Number.MAX_SAFE_INTEGER 排在最后 */
  const byConversation = ref<Record<string, LocalMessage[]>>({})
  /** blobId → 版本号；缩略图生成完成（MessageUpdated）后 +1，强制 img 重新请求 */
  const blobVersion = ref<Record<string, number>>({})

  function list(conversationId: string): LocalMessage[] {
    return byConversation.value[conversationId] ?? (byConversation.value[conversationId] = [])
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

  /** 先读 IndexedDB 秒开，再拉最新一页 */
  async function load(conversationId: string, limit = 50): Promise<void> {
    const cached = await db.messages.where('[conversationId+seq]').between([conversationId, 0], [conversationId, Infinity]).toArray()
    if (cached.length) {
      const target = list(conversationId)
      for (const m of cached) upsert(target, m)
    }
    const { data } = await api.GET('/api/conversations/{conversationId}/messages', {
      params: { path: { conversationId }, query: { limit } },
    })
    if (data) await applyIncoming(data)
  }

  /** 服务端来的消息（实时、Sync、分页）统一走这里：替换 pending、写缓存、更新会话摘要 */
  async function applyIncoming(messages: MessageDto[], opts: { bumpBlobs?: boolean } = {}): Promise<void> {
    const conversations = useConversationsStore()
    for (const m of messages) {
      const local: LocalMessage = { ...m, status: 'sent' }
      upsert(list(m.conversationId), local)
      conversations.bumpLastSeq(m.conversationId, m.seq, previewOf(m), m.createdAt)
      pendingFiles.delete(m.clientMessageId)
      uploads.delete(m.clientMessageId)
      if (opts.bumpBlobs) {
        for (const a of m.attachments) blobVersion.value[a.blobId] = (blobVersion.value[a.blobId] ?? 0) + 1
      }
    }
    await db.messages.bulkPut(messages.map((m) => ({ ...m, status: 'sent' as const })))
  }

  function newPending(conversationId: string, partial: Partial<LocalMessage>): LocalMessage {
    const session = useSessionStore()
    const clientMessageId = crypto.randomUUID()
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

  /** 乐观发送：先插一条 pending，服务端回执（MessageReceived）按 clientMessageId 替换 */
  async function sendText(conversationId: string, body: string): Promise<void> {
    const pending = newPending(conversationId, { type: 'Text', body })
    upsert(list(conversationId), pending)
    try {
      await sendMessage({ conversationId, clientMessageId: pending.clientMessageId, type: 'Text', body })
    } catch {
      pending.status = 'failed'
      upsert(list(conversationId), { ...pending })
    }
  }

  /** 每个文件一条消息：先显示本地预览与进度，上传完成后再发 SignalR */
  async function sendFiles(conversationId: string, files: File[]): Promise<void> {
    await Promise.all(files.map((f) => sendFile(conversationId, f)))
  }

  async function sendFile(conversationId: string, file: File): Promise<void> {
    const kind = kindOf(file)
    const size = kind === 'Image' ? await imageSize(file) : null
    const meta = JSON.stringify({ name: file.name, size: file.size, mime: file.type || 'application/octet-stream', ...(size ?? {}) })
    const pending = newPending(conversationId, {
      type: kind,
      attachments: [{ id: '', blobId: '', kind, meta }],
      uploadProgress: 0,
      localUrl: kind === 'Image' || kind === 'Video' ? URL.createObjectURL(file) : undefined,
    })
    pendingFiles.set(pending.clientMessageId, file)
    upsert(list(conversationId), pending)
    await uploadAndSend(conversationId, pending.clientMessageId)
  }

  async function uploadAndSend(conversationId: string, clientMessageId: string): Promise<void> {
    const msgs = list(conversationId)
    const m = msgs.find((x) => x.clientMessageId === clientMessageId)
    const file = pendingFiles.get(clientMessageId)
    if (!m || !file) return
    const att = m.attachments[0]!
    try {
      const handle = uploadFile(file, { onProgress: (r) => { m.uploadProgress = r } })
      uploads.set(clientMessageId, handle)
      const blobId = await handle.done
      att.blobId = blobId
      await sendMessage({
        conversationId,
        clientMessageId,
        type: m.type,
        attachments: [{ blobId, kind: att.kind, meta: att.meta }],
      })
    } catch {
      m.status = 'failed'
      upsert(msgs, { ...m })
    } finally {
      uploads.delete(clientMessageId)
    }
  }

  async function retry(conversationId: string, clientMessageId: string): Promise<void> {
    const msgs = list(conversationId)
    const m = msgs.find((x) => x.clientMessageId === clientMessageId)
    if (!m) return
    m.status = 'pending'
    if (m.type === 'Text') {
      if (!m.body) return
      try {
        await sendMessage({ conversationId, clientMessageId, type: 'Text', body: m.body })
      } catch {
        m.status = 'failed'
      }
      return
    }
    // 附件：已拿到 blobId 就直接重发，否则重新上传
    const att = m.attachments[0]
    if (att?.blobId) {
      try {
        await sendMessage({ conversationId, clientMessageId, type: m.type, attachments: [{ blobId: att.blobId, kind: att.kind, meta: att.meta }] })
      } catch {
        m.status = 'failed'
      }
    } else if (pendingFiles.has(clientMessageId)) {
      await uploadAndSend(conversationId, clientMessageId)
    } else {
      // 刷新过页面，File 对象没了：删掉这条，让用户重选
      cancel(conversationId, clientMessageId)
    }
  }

  function cancel(conversationId: string, clientMessageId: string): void {
    void uploads.get(clientMessageId)?.abort()
    uploads.delete(clientMessageId)
    pendingFiles.delete(clientMessageId)
    const msgs = list(conversationId)
    const idx = msgs.findIndex((x) => x.clientMessageId === clientMessageId)
    if (idx >= 0) {
      const [m] = msgs.splice(idx, 1)
      if (m?.localUrl) URL.revokeObjectURL(m.localUrl)
    }
  }

  async function read(conversationId: string): Promise<void> {
    const msgs = list(conversationId).filter((m) => m.status === 'sent')
    const last = msgs[msgs.length - 1]
    if (!last) return
    const conversations = useConversationsStore()
    const c = conversations.byId(conversationId)
    if (c && last.seq > c.lastReadSeq) {
      c.lastReadSeq = last.seq
      await markRead(conversationId, last.seq)
    }
  }

  return { byConversation, blobVersion, list, lastSeqs, load, applyIncoming, sendText, sendFiles, retry, cancel, read }
})
