import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, type ConversationDto } from '@/api/client'
import type { components } from '@/api/schema'
import { useSessionStore } from './session'

export type MemberDto = components['schemas']['MemberDto']

/** 家庭成员名单：通话界面、单聊标题都要把用户 Id 变成名字 */
export const useFamilyStore = defineStore('family', () => {
  const members = ref<MemberDto[]>([])
  const loaded = ref(false)
  let loading: Promise<void> | null = null
  /** 名单里查不到、已经为它刷新过一次的 Id：不再反复请求 */
  const unknown = new Set<string>()

  /** 进应用后拉一次；force 用于遇到不认识的人（新成员刚加入）时刷新 */
  function load(force = false): Promise<void> {
    if (loaded.value && !force) return Promise.resolve()
    loading ??= (async () => {
      try {
        const { data } = await api.GET('/api/family/members')
        if (data) {
          members.value = data
          loaded.value = true
        }
      } catch {
        // 离线：名字先显示占位，联网后再拉
      } finally {
        loading = null
      }
    })()
    return loading
  }

  function byId(id: string | null | undefined): MemberDto | undefined {
    return id ? members.value.find((m) => m.id === id) : undefined
  }

  function nameOf(id: string | null | undefined): string {
    const m = byId(id)
    if (m) return m.displayName || m.username
    if (id && loaded.value && !unknown.has(id)) {
      unknown.add(id)
      void load(true)
    }
    return '家人'
  }

  /** 会话显示名：群聊用群名，单聊用对方的名字 */
  function titleOf(c: ConversationDto | null | undefined): string {
    if (!c) return ''
    if (c.title) return c.title
    const me = useSessionStore().user?.id
    const others = c.memberIds.filter((id) => id !== me)
    if (!others.length) return useSessionStore().user?.displayName ?? ''
    return others.map((id) => nameOf(id)).join('、')
  }

  return { members, loaded, load, byId, nameOf, titleOf }
})
