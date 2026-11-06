import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api, type ConversationDto } from '@/api/client'
import { db } from '@/db'
import type { ReadReceiptEvent } from '@/realtime/connection'

export const useConversationsStore = defineStore('conversations', () => {
  const items = ref<ConversationDto[]>([])
  const loaded = ref(false)

  const sorted = computed(() =>
    [...items.value].sort((a, b) => (b.lastMessageAt ?? '').localeCompare(a.lastMessageAt ?? '')),
  )

  const unreadTotal = computed(() =>
    items.value.reduce((n, c) => n + Math.max(0, c.lastSeq - c.lastReadSeq), 0),
  )

  function byId(id: string): ConversationDto | undefined {
    return items.value.find((c) => c.id === id)
  }

  /** 先用 IndexedDB 副本秒开，再从服务端刷新 */
  async function load(): Promise<void> {
    if (!loaded.value) {
      const cached = await db.conversations.toArray()
      if (cached.length) items.value = cached
    }
    const { data } = await api.GET('/api/conversations')
    if (data) {
      items.value = data
      await db.conversations.bulkPut(data)
    }
    loaded.value = true
  }

  function applyReadReceipt(e: ReadReceiptEvent): void {
    // 自己的已读回执更新本地 lastReadSeq；他人的留给 M1 做已读头像
    const c = byId(e.conversationId)
    if (c && e.seq > c.lastReadSeq) c.lastReadSeq = e.seq
  }

  function bumpLastSeq(conversationId: string, seq: number, preview?: string | null, at?: string): void {
    const c = byId(conversationId)
    if (!c) return
    if (seq > c.lastSeq) {
      c.lastSeq = seq
      c.lastMessagePreview = preview ?? c.lastMessagePreview
      c.lastMessageAt = at ?? c.lastMessageAt
    }
  }

  return { items, sorted, unreadTotal, loaded, byId, load, applyReadReceipt, bumpLastSeq }
})
