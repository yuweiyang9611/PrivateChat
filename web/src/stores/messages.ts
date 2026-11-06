import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, type MessageDto } from '@/api/client'
import { db, type LocalMessage } from '@/db'
import { markRead, sendMessage } from '@/realtime/connection'
import { useConversationsStore } from './conversations'
import { useSessionStore } from './session'

/** 按 seq 有序插入或替换（同 id / 同 clientMessageId 视为同一条） */
function upsert(list: LocalMessage[], m: LocalMessage): void {
  const idx = list.findIndex((x) => x.id === m.id || (x.clientMessageId && x.clientMessageId === m.clientMessageId))
  if (idx >= 0) {
    list[idx] = m
  } else {
    list.push(m)
  }
  list.sort((a, b) => a.seq - b.seq)
}

export const useMessagesStore = defineStore('messages', () => {
  /** conversationId → 按 seq 升序的消息；pending 消息 seq 为 Number.MAX_SAFE_INTEGER 排在最后 */
  const byConversation = ref<Record<string, LocalMessage[]>>({})

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
  async function applyIncoming(messages: MessageDto[]): Promise<void> {
    const conversations = useConversationsStore()
    for (const m of messages) {
      const local: LocalMessage = { ...m, status: 'sent' }
      upsert(list(m.conversationId), local)
      conversations.bumpLastSeq(m.conversationId, m.seq, m.body, m.createdAt)
    }
    await db.messages.bulkPut(messages.map((m) => ({ ...m, status: 'sent' as const })))
  }

  /** 乐观发送：先插一条 pending，服务端回执（MessageReceived）按 clientMessageId 替换 */
  async function sendText(conversationId: string, body: string): Promise<void> {
    const session = useSessionStore()
    const clientMessageId = crypto.randomUUID()
    const pending: LocalMessage = {
      id: `local-${clientMessageId}`,
      conversationId,
      seq: Number.MAX_SAFE_INTEGER,
      senderId: session.user?.id ?? '',
      clientMessageId,
      type: 'Text',
      body,
      payload: null,
      replyToId: null,
      createdAt: new Date().toISOString(),
      editedAt: null,
      deleted: false,
      attachments: [],
      reactions: [],
      status: 'pending',
    }
    upsert(list(conversationId), pending)
    try {
      await sendMessage({ conversationId, clientMessageId, type: 'Text', body })
    } catch {
      pending.status = 'failed'
      upsert(list(conversationId), { ...pending })
    }
  }

  async function retry(conversationId: string, clientMessageId: string): Promise<void> {
    const msgs = list(conversationId)
    const m = msgs.find((x) => x.clientMessageId === clientMessageId)
    if (!m || !m.body) return
    m.status = 'pending'
    try {
      await sendMessage({ conversationId, clientMessageId, type: 'Text', body: m.body })
    } catch {
      m.status = 'failed'
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

  return { byConversation, list, lastSeqs, load, applyIncoming, sendText, retry, read }
})
