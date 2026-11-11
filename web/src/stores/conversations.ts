import { defineStore } from 'pinia'
import { computed, ref, toRaw } from 'vue'
import { api, type ConversationDto } from '@/api/client'
import { db } from '@/db'
import type { ReadReceiptEvent } from '@/realtime/connection'
import { useSessionStore } from './session'

export const useConversationsStore = defineStore('conversations', () => {
  const items = ref<ConversationDto[]>([])
  const loaded = ref(false)
  /** 列表是否已和服务器对过（离线启动时只有本地副本，连上后要再拉一次） */
  const fresh = ref(false)

  const sorted = computed(() =>
    [...items.value].sort((a, b) => (b.lastMessageAt ?? '').localeCompare(a.lastMessageAt ?? '')),
  )

  const unreadTotal = computed(() =>
    items.value.reduce((n, c) => n + Math.max(0, c.lastSeq - c.lastReadSeq), 0),
  )

  function byId(id: string): ConversationDto | undefined {
    return items.value.find((c) => c.id === id)
  }

  /** 先用 IndexedDB 副本秒开，再从服务端刷新；离线时只用副本 */
  async function load(): Promise<void> {
    if (!loaded.value) {
      const cached = await db.conversations.toArray()
      if (cached.length) items.value = cached
    }
    try {
      const { data } = await api.GET('/api/conversations')
      if (data) {
        items.value = data
        fresh.value = true
        // 服务器列表为准：退出 / 被移出的会话从本地副本里去掉
        await db.transaction('rw', db.conversations, async () => {
          await db.conversations.clear()
          await db.conversations.bulkPut(data)
        })
      }
    } catch {
      // 离线 / 服务器不可达：沿用本地副本
    } finally {
      loaded.value = true
    }
  }

  /** 摘要 / 未读变化写回本地副本，离线冷启动时列表才是新的。合并成每 300 ms 一次批量写 */
  const dirty = new Set<string>()
  let timer: ReturnType<typeof setTimeout> | undefined
  function touch(id: string): void {
    dirty.add(id)
    if (timer) return
    timer = setTimeout(() => {
      timer = undefined
      const rows = [...dirty].map((x) => byId(x)).filter((c): c is ConversationDto => !!c).map((c) => JSON.parse(JSON.stringify(toRaw(c))) as ConversationDto)
      dirty.clear()
      if (rows.length) void db.conversations.bulkPut(rows).catch(() => undefined)
    }, 300)
  }

  function applyReadReceipt(e: ReadReceiptEvent): void {
    // 自己的已读回执（别的设备上读了、通话记录自动已读）更新本地 lastReadSeq；他人的留给后续做已读头像
    if (e.userId !== useSessionStore().user?.id) return
    const c = byId(e.conversationId)
    if (c && e.seq > c.lastReadSeq) {
      c.lastReadSeq = e.seq
      touch(c.id)
    }
  }

  /** 来了不认识的会话的消息（别人刚拉我进群、离线期间新建的会话）：合并成一次刷新列表 */
  let reloadTimer: ReturnType<typeof setTimeout> | undefined
  function requestReload(): void {
    if (!loaded.value || reloadTimer) return
    reloadTimer = setTimeout(() => {
      reloadTimer = undefined
      void load()
    }, 500)
  }

  function bumpLastSeq(conversationId: string, seq: number, preview?: string | null, at?: string): void {
    const c = byId(conversationId)
    if (!c) {
      requestReload()
      return
    }
    if (seq > c.lastSeq) {
      c.lastSeq = seq
      c.lastMessagePreview = preview ?? c.lastMessagePreview
      c.lastMessageAt = at ?? c.lastMessageAt
      touch(c.id)
    }
  }

  return { items, sorted, unreadTotal, loaded, fresh, byId, load, touch, applyReadReceipt, bumpLastSeq }
})
