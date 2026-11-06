import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { ref } from 'vue'
import type { MessageDto } from '@/api/client'
import { useMessagesStore } from '@/stores/messages'
import { useConversationsStore } from '@/stores/conversations'
import { reloadForAccessLogin } from '@/api/client'

export interface ReadReceiptEvent { conversationId: string; userId: string; seq: number }
export interface TypingEvent { conversationId: string; userId: string }
export interface PresenceEvent { userId: string; online: boolean; lastSeenAt?: string | null }
export interface IncomingCallEvent { callId: string; conversationId: string; type: 'Audio' | 'Video' | 'Meeting'; startedBy: string }

export const connectionState = ref<HubConnectionState>(HubConnectionState.Disconnected)
export const presence = ref<Record<string, PresenceEvent>>({})
export const typing = ref<Record<string, string[]>>({})
export const incomingCall = ref<IncomingCallEvent | null>(null)

let connection: HubConnection | null = null
let consecutiveFailures = 0
let stopped = false

/** SignalR 单例：/hubs/chat，Cookie 认证，自动重连；重连后调用 Sync 补拉缺失消息 */
export function getConnection(): HubConnection {
  if (connection) return connection

  connection = new HubConnectionBuilder()
    .withUrl('/hubs/chat')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  // SignalR 默认 15 s keep-alive，已覆盖 Cloudflare 约 100 s 的空闲超时
  connection.keepAliveIntervalInMilliseconds = 15_000

  connection.onreconnecting(() => { connectionState.value = HubConnectionState.Reconnecting })
  connection.onreconnected(async () => {
    connectionState.value = HubConnectionState.Connected
    consecutiveFailures = 0
    await sync()
  })
  connection.onclose(() => {
    connectionState.value = HubConnectionState.Disconnected
    if (stopped) return
    // 自动重连耗尽仍失败：多半是 Access Cookie 过期，整页刷新去重新验证
    if (++consecutiveFailures >= 2) reloadForAccessLogin()
  })

  registerHandlers(connection)
  return connection
}

export async function startConnection(): Promise<void> {
  stopped = false
  const conn = getConnection()
  if (conn.state !== HubConnectionState.Disconnected) return
  connectionState.value = HubConnectionState.Connecting
  try {
    await conn.start()
    connectionState.value = HubConnectionState.Connected
    consecutiveFailures = 0
    await sync()
  } catch {
    connectionState.value = HubConnectionState.Disconnected
    // 启动失败：5 s 后再试，由调用方决定是否放弃
    setTimeout(() => { if (!stopped) void startConnection() }, 5_000)
  }
}

/** 退出登录时主动断开，不触发自动重连与整页刷新 */
export async function stopConnection(): Promise<void> {
  stopped = true
  if (connection && connection.state !== HubConnectionState.Disconnected) await connection.stop()
  connectionState.value = HubConnectionState.Disconnected
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
  conn.on('MessageUpdated', (m: MessageDto) => void useMessagesStore().applyIncoming([m]))
  conn.on('MessageDeleted', (m: MessageDto) => void useMessagesStore().applyIncoming([m]))
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
  conn.on('IncomingCall', (e: IncomingCallEvent) => { incomingCall.value = e })
  conn.on('CallUpdated', () => { /* M3：通话状态 */ })
  conn.on('Notification', () => { /* M1：站内通知 */ })
}

export async function sendMessage(dto: {
  conversationId: string
  clientMessageId: string
  type: MessageDto['type']
  body?: string
}): Promise<void> {
  await getConnection().invoke('SendMessage', dto)
}

export async function markRead(conversationId: string, seq: number): Promise<void> {
  const conn = getConnection()
  if (conn.state === HubConnectionState.Connected) await conn.invoke('MarkRead', conversationId, seq)
}

export async function notifyTyping(conversationId: string): Promise<void> {
  const conn = getConnection()
  if (conn.state === HubConnectionState.Connected) await conn.send('Typing', conversationId)
}
