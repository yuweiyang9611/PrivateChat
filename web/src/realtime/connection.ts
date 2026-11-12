import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel, type IRetryPolicy, type RetryContext } from '@microsoft/signalr'
import { ref } from 'vue'
import { fetchMe, handleAuthLoss, reauthenticate, type MessageDto } from '@/api/client'
import { isOnline, onOnline } from '@/net/online'
import { useMessagesStore } from '@/stores/messages'
import { useConversationsStore } from '@/stores/conversations'
import { useDriveStore } from '@/stores/drive'
import { useMomentsStore, type AlbumChangedEvent, type PostChangedEvent } from '@/stores/moments'
import { useCallsStore, type CallDeclinedEvent, type ParticipantEvent, type ParticipantLeftEvent, type TrackEvent } from '@/stores/calls'
import type { CallDto } from '@/features/calls/api'

export interface ReactionsEvent { messageId: string; conversationId: string; seq: number; reactions: { userId: string; emoji: string }[] }
export interface MessageDeletedEvent { messageId: string; conversationId: string; seq: number }
export interface ReadReceiptEvent { conversationId: string; userId: string; seq: number }
export interface TypingEvent { conversationId: string; userId: string }
export interface PresenceEvent { userId: string; online: boolean; lastSeenAt?: string | null }

export const connectionState = ref<HubConnectionState>(HubConnectionState.Disconnected)
export const presence = ref<Record<string, PresenceEvent>>({})
export const typing = ref<Record<string, string[]>>({})


let connection: HubConnection | null = null
/** 登录后为 true；退出登录 / 会话失效后为 false，此时不再自动重连 */
let wanted = false
let starting: Promise<void> | null = null
let retryTimer: number | undefined
let retryAttempt = 0

/** 我们自己的重连退避（SignalR 内置重连放弃之后接手） */
const RETRY_DELAYS_MS = [3_000, 5_000, 10_000, 20_000, 30_000, 60_000]
/** SignalR 内置的快速重连：约 1 分钟内试 6 次；离线时立即放弃，等 online 事件 */
const QUICK_RETRY_MS = [0, 2_000, 5_000, 10_000, 20_000, 30_000]
const retryPolicy: IRetryPolicy = {
  nextRetryDelayInMilliseconds(ctx: RetryContext): number | null {
    if (!isOnline()) return null
    return ctx.previousRetryCount < QUICK_RETRY_MS.length ? QUICK_RETRY_MS[ctx.previousRetryCount]! : null
  },
}

/** SignalR 单例：/hubs/chat，Cookie 认证；连上（含重连）后补拉缺失消息、补发发件箱 */
export function getConnection(): HubConnection {
  if (connection) return connection

  connection = new HubConnectionBuilder()
    .withUrl('/hubs/chat')
    .withAutomaticReconnect(retryPolicy)
    .configureLogging(LogLevel.Warning)
    .build()

  // 15 s 心跳，覆盖 Cloudflare 约 100 s 的空闲超时
  connection.keepAliveIntervalInMilliseconds = 15_000

  connection.onreconnecting(() => { connectionState.value = HubConnectionState.Reconnecting })
  connection.onreconnected(() => {
    connectionState.value = HubConnectionState.Connected
    retryAttempt = 0
    void onConnected()
  })
  connection.onclose(() => {
    connectionState.value = HubConnectionState.Disconnected
    if (wanted) void recover()
  })

  registerHandlers(connection)
  return connection
}

export function isConnected(): boolean {
  return connection?.state === HubConnectionState.Connected
}

/** 登录后调用；离线时什么都不做，等 online 事件；失败后按退避重试 */
export async function startConnection(): Promise<void> {
  wanted = true
  window.clearTimeout(retryTimer)
  const conn = getConnection()
  if (conn.state !== HubConnectionState.Disconnected) return
  if (!isOnline()) return
  if (starting) return starting
  starting = (async () => {
    connectionState.value = HubConnectionState.Connecting
    try {
      await conn.start()
      connectionState.value = HubConnectionState.Connected
      retryAttempt = 0
      void onConnected()
    } catch {
      connectionState.value = HubConnectionState.Disconnected
      await recover()
    } finally {
      starting = null
    }
  })()
  return starting
}

/** 退出登录 / 会话失效时主动断开，不再重连 */
export async function stopConnection(): Promise<void> {
  wanted = false
  window.clearTimeout(retryTimer)
  if (connection && connection.state !== HubConnectionState.Disconnected) await connection.stop()
  connectionState.value = HubConnectionState.Disconnected
}

/**
 * 连接失败 / 断开后先查清原因再决定：
 * 离线 → 等 online 事件；登录态丢失 → 交给会话处理（Access 重新验证或回登录页）；
 * 其余（服务器重启、隧道抖动）→ 退避重试。
 */
async function recover(): Promise<void> {
  if (!wanted) return
  const { probe } = await fetchMe()
  if (!wanted) return
  if (probe === 'unauthorized') {
    wanted = false
    handleAuthLoss(probe)
    return
  }
  if (probe === 'access-expired') {
    if (reauthenticate()) {
      wanted = false
      return
    }
    // 60 秒冷却期内不重复跳转：按退避继续重试，冷却过后下一次探测会再去重新验证
  }
  if (probe === 'offline') return
  const delay = RETRY_DELAYS_MS[Math.min(retryAttempt++, RETRY_DELAYS_MS.length - 1)]!
  window.clearTimeout(retryTimer)
  retryTimer = window.setTimeout(() => void startConnection(), delay)
}

/** 每次连上：（离线启动时）刷新会话列表 → 补拉断线期间的消息 → 补发发件箱 → 补发离线时的已读 */
async function onConnected(): Promise<void> {
  const messages = useMessagesStore()
  const conversations = useConversationsStore()
  // 离线 / 服务器不通时启动的，列表只是本地副本：先对一次，Sync 补拉的新会话消息才有地方放
  if (!conversations.fresh) await conversations.load()
  try {
    await sync()
  } catch {
    // 补拉失败不影响收发，下次连上再补
  }
  void messages.flushOutbox()
  void messages.flushPendingReads()
  // 服务器重启 / 隧道恢复后网盘的出错、离线状态也要恢复（没打开过网盘时什么也不做）
  useDriveStore().resync()
  // 断线期间错过的动态和相册改动（没打开过时光页时什么也不做）
  useMomentsStore().resync()
  // 断线期间的来电、通话结束、有人进出都没收到：和服务器对一遍
  void useCallsStore().refresh()
}

if (typeof window !== 'undefined') {
  // 恢复联网：立刻重连，并继续之前因断网暂停的附件上传
  onOnline(() => {
    if (!wanted) return
    void startConnection()
    void useMessagesStore().flushOutbox()
  })
  // 手机切回前台：后台时连接常被系统掐掉，立刻重连而不是等退避计时器
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && wanted) void startConnection()
  })
}

interface SyncResult { messages: Record<string, MessageDto[]>; needFullReload: string[] }

/** 把每个会话本地最大 seq 发给服务端，拿回缺失的消息（服务端按会话分组返回） */
async function sync(): Promise<void> {
  const messages = useMessagesStore()
  const lastSeqs = messages.lastSeqs()
  const result = await getConnection().invoke<SyncResult>('Sync', lastSeqs)
  const missed = Object.values(result?.messages ?? {}).flat()
  if (missed.length) await messages.applyIncoming(missed)
  if (result?.needFullReload?.length) await useConversationsStore().load()
}

function registerHandlers(conn: HubConnection): void {
  conn.on('MessageReceived', (m: MessageDto) => void useMessagesStore().applyIncoming([m]))
  // 缩略图 / 转码完成走 MessageUpdated：只刷新已有的消息，附件 blob 版本 +1 让 img 重新取图
  conn.on('MessageUpdated', (m: MessageDto) => void useMessagesStore().applyUpdated(m))
  conn.on('MessageDeleted', (e: MessageDeletedEvent) => void useMessagesStore().markDeleted(e))
  conn.on('ReactionChanged', (e: ReactionsEvent) => void useMessagesStore().applyReactions(e))
  conn.on('ReadReceipt', (e: ReadReceiptEvent) => useConversationsStore().applyReadReceipt(e))
  conn.on('Typing', (e: TypingEvent) => {
    const list = new Set(typing.value[e.conversationId] ?? [])
    list.add(e.userId)
    typing.value = { ...typing.value, [e.conversationId]: [...list] }
    setTimeout(() => {
      const cur = new Set(typing.value[e.conversationId] ?? [])
      cur.delete(e.userId)
      typing.value = { ...typing.value, [e.conversationId]: [...cur] }
    }, 4_000)
  })
  conn.on('Presence', (e: PresenceEvent) => { presence.value = { ...presence.value, [e.userId]: e } })
  conn.on('ConversationUpdated', () => void useConversationsStore().load())
  conn.on('DriveChanged', (e: { spaceId: string; parentId?: string | null }) => useDriveStore().onChanged(e))
  // 时光：有人发了 / 评论了 / 点赞了 / 删了动态，相册被改了
  conn.on('PostChanged', (e: PostChangedEvent) => useMomentsStore().onPostChanged(e))
  conn.on('AlbumChanged', (e: AlbumChangedEvent) => useMomentsStore().onAlbumChanged(e))
  conn.on('IncomingCall', (c: CallDto) => useCallsStore().onIncomingCall(c))
  conn.on('CallUpdated', (c: CallDto) => useCallsStore().onCallUpdated(c))
  conn.on('CallDeclined', (e: CallDeclinedEvent) => useCallsStore().onCallDeclined(e))
  conn.on('ParticipantJoined', (e: ParticipantEvent) => useCallsStore().onParticipantJoined(e))
  conn.on('ParticipantLeft', (e: ParticipantLeftEvent) => useCallsStore().onParticipantLeft(e))
  conn.on('ParticipantUpdated', (e: ParticipantEvent) => useCallsStore().onParticipantUpdated(e))
  conn.on('TrackPublished', (e: TrackEvent) => useCallsStore().onTrackPublished(e))
  conn.on('TrackUnpublished', (e: TrackEvent) => useCallsStore().onTrackUnpublished(e))
  conn.on('Notification', () => { /* M1：站内通知 */ })
}

export interface SendMessageDto {
  conversationId: string
  clientMessageId: string
  type: MessageDto['type']
  body?: string | null
  attachments?: { blobId: string; kind: MessageDto['attachments'][number]['kind']; meta: string | null }[]
}

/** 发送并返回服务器确认的消息（幂等：同一 clientMessageId 重发返回同一条） */
export async function sendMessage(dto: SendMessageDto): Promise<MessageDto> {
  return await getConnection().invoke<MessageDto>('SendMessage', dto)
}

export async function react(messageId: string, emoji: string): Promise<ReactionsEvent> {
  return await getConnection().invoke<ReactionsEvent>('React', messageId, emoji)
}

/** 返回是否已送达服务器；没连上时返回 false，由调用方稍后补发 */
export async function markRead(conversationId: string, seq: number): Promise<boolean> {
  if (!isConnected()) return false
  try {
    await getConnection().invoke('MarkRead', conversationId, seq)
    return true
  } catch {
    return false
  }
}

export async function notifyTyping(conversationId: string): Promise<void> {
  if (isConnected()) await getConnection().send('Typing', conversationId)
}
