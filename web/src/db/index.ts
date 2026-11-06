import Dexie, { type EntityTable } from 'dexie'
import type { ConversationDto, MessageDto } from '@/api/client'

/** 附件 meta（jsonb 字符串）解析后的结构，由发送端写入 */
export interface AttachmentMeta {
  name?: string
  size?: number
  mime?: string
  width?: number
  height?: number
  duration?: number
}

/** 本地消息副本，带仅本地使用的发送状态与上传进度（不会写入 IndexedDB 之外的地方） */
export type LocalMessage = MessageDto & {
  status?: 'pending' | 'sent' | 'failed'
  /** 附件上传进度 0–1，仅 pending 时有意义 */
  uploadProgress?: number
  /** 本地预览 URL（object URL），发送成功后释放 */
  localUrl?: string
}

export class PrivateChatDb extends Dexie {
  messages!: EntityTable<LocalMessage, 'id'>
  conversations!: EntityTable<ConversationDto, 'id'>

  constructor() {
    super('privatechat')
    this.version(1).stores({
      // 复合索引 [conversationId+seq] 用于按会话顺序读取；clientMessageId 用于回执匹配
      messages: 'id, [conversationId+seq], conversationId, clientMessageId',
      conversations: 'id, lastMessageAt',
    })
  }
}

export const db = new PrivateChatDb()

export function parseMeta(meta: string | null | undefined): AttachmentMeta {
  if (!meta) return {}
  try {
    return JSON.parse(meta) as AttachmentMeta
  } catch {
    return {}
  }
}
