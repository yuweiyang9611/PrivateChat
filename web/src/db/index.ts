import Dexie, { type EntityTable } from 'dexie'
import type { ConversationDto, MessageDto } from '@/api/client'

/** 本地消息副本，带一个仅本地使用的发送状态 */
export type LocalMessage = MessageDto & {
  status?: 'pending' | 'sent' | 'failed'
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
