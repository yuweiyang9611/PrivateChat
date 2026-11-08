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

/** 本地消息副本，带仅本地使用的发送状态与上传进度 */
export type LocalMessage = MessageDto & {
  /** pending：排队 / 发送中（离线时等网络）；failed：服务器拒绝或文件丢失，需要手动重试 */
  status?: 'pending' | 'sent' | 'failed'
  /** 附件上传进度 0–1，仅内存 */
  uploadProgress?: number
  /** 本地预览 URL（object URL），仅内存 */
  localUrl?: string
}

/**
 * 发件箱：还没被服务器确认的自己发的消息。刷新页面、断网、关掉 App 都不丢，联网后按入队顺序补发。
 * 服务器按 (发送者, clientMessageId) 幂等，重复补发不会产生重复消息。
 */
export interface OutboxEntry {
  clientMessageId: string
  conversationId: string
  /** 入队时间（毫秒），决定补发顺序 */
  queuedAt: number
  /** 乐观消息快照（纯对象，不含 localUrl / uploadProgress） */
  message: LocalMessage
  /** 附件原文件，便于刷新后续传；超过 OUTBOX_FILE_LIMIT 的不存 */
  file?: Blob
  fileName?: string
}

/** 发件箱里最多保存多大的附件原文件（语音、照片都远小于这个值） */
export const OUTBOX_FILE_LIMIT = 50 * 1024 * 1024

/** 网盘目录的离线副本：上次在线时看到的内容 */
export interface CachedListing {
  key: string
  listing: unknown
  savedAt: number
}

export class PrivateChatDb extends Dexie {
  messages!: EntityTable<LocalMessage, 'id'>
  driveListings!: EntityTable<CachedListing, 'key'>
  conversations!: EntityTable<ConversationDto, 'id'>
  outbox!: EntityTable<OutboxEntry, 'clientMessageId'>

  constructor() {
    super('privatechat')
    this.version(1).stores({
      // 复合索引 [conversationId+seq] 用于按会话顺序读取；clientMessageId 用于回执匹配
      messages: 'id, [conversationId+seq], conversationId, clientMessageId',
      conversations: 'id, lastMessageAt',
    })
    this.version(2).stores({
      outbox: 'clientMessageId, conversationId, queuedAt',
    })
    this.version(3).stores({
      driveListings: 'key, savedAt',
    })
  }
}

export const db = new PrivateChatDb()

const deletedElsewhere = new Set<() => void>()

/**
 * 别的标签页（退出登录 / 换账号）要删库时：Dexie 默认关闭后会自动重开，本页下一次写入就把上一个人的数据又建回来。
 * 这里改成关闭后不再自动打开，并通知会话层。升级版本（新版本上线）仍交给 Dexie 默认处理。
 */
db.on('versionchange', (ev: IDBVersionChangeEvent) => {
  if (ev.newVersion) return undefined
  db.close({ disableAutoOpen: true })
  for (const cb of deletedElsewhere) cb()
  return false
})

export function onDbDeletedElsewhere(cb: () => void): void {
  deletedElsewhere.add(cb)
}

/** 本页被别的标签页关掉库之后，又在本页登录时重新打开 */
export async function ensureDbOpen(): Promise<void> {
  if (!db.isOpen()) await db.open().catch(() => undefined)
}

export function parseMeta(meta: string | null | undefined): AttachmentMeta {
  if (!meta) return {}
  try {
    return JSON.parse(meta) as AttachmentMeta
  } catch {
    return {}
  }
}

/** 删除整个本地库后重新打开（退出登录 / 换账号） */
export async function resetDb(): Promise<void> {
  await db.delete({ disableAutoOpen: false })
}
