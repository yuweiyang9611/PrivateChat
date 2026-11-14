import { db, parseMeta, type LocalMessage } from '@/db'
import type { components } from '@/api/schema'
import { matchesAll, snippet } from './text'

export type MessageHit = components['schemas']['MessageHitDto']

/**
 * 离线时的搜索：只在这台设备保存过的聊天记录里找（打开过的会话、实时收到过的消息），新的在前。
 * 只看本地会话列表里还在的会话；撤回的、还没发出去的不算。
 */
export async function searchLocalMessages(terms: readonly string[], limit = 50): Promise<MessageHit[]> {
  if (!terms.length) return []
  const conversations = new Set((await db.conversations.toArray()).map((c) => c.id))
  const rows = await db.messages.filter((m) => conversations.has(m.conversationId) && matchLocal(m, terms) !== null).toArray()
  return rows
    .map((m) => matchLocal(m, terms)!)
    .sort((a, b) => (a.createdAt < b.createdAt ? 1 : a.createdAt > b.createdAt ? -1 : 0))
    .slice(0, limit)
}

/** 一条本地消息命中时返回和服务端同样形状的结果，否则 null */
export function matchLocal(m: LocalMessage, terms: readonly string[]): MessageHit | null {
  if (m.deleted || (m.status && m.status !== 'sent')) return null
  const base = { messageId: m.id, conversationId: m.conversationId, seq: m.seq, senderId: m.senderId, createdAt: m.createdAt }
  if (m.type === 'Text' && matchesAll(m.body, terms))
    return { ...base, kind: 'text', snippet: snippet(m.body ?? '', terms), blobId: null, fileName: null, mime: null, attachmentKind: null }
  for (const a of m.attachments) {
    const meta = parseMeta(a.meta)
    if (meta.name && matchesAll(meta.name, terms))
      return { ...base, kind: 'file', snippet: meta.name, blobId: a.blobId, fileName: meta.name, mime: meta.mime ?? null, attachmentKind: a.kind }
  }
  return null
}
