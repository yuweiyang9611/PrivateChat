import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, type ConversationDto } from '@/api/client'
import type { components } from '@/api/schema'
import { useSessionStore } from './session'

export type MemberDto = components['schemas']['MemberDto']
export type InviteDto = components['schemas']['InviteDto']
export type UserRole = components['schemas']['UserRole']

/** 接口错误：带服务器的说明（ProblemDetails.detail / title） */
export class FamilyApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

type Result<T> = { data?: T; error?: unknown; response: Response }

async function unwrap<T>(request: Promise<Result<T>>): Promise<T> {
  let r: Result<T>
  try {
    r = await request
  } catch {
    throw new FamilyApiError(0, '网络不通')
  }
  if (!r.response.ok) {
    const problem = r.error as { title?: string; detail?: string | null } | undefined
    throw new FamilyApiError(r.response.status, problem?.detail || problem?.title || `HTTP ${r.response.status}`)
  }
  return r.data as T
}

/**
 * 家庭成员名单：通话界面、单聊标题都要把用户 Id 变成名字。
 * 管理员另外能看 / 发 / 撤销邀请，改成员的名字、角色，停用和恢复成员（家庭页）。
 */
export const useFamilyStore = defineStore('family', () => {
  const members = ref<MemberDto[]>([])
  const loaded = ref(false)
  /** 还没用过的邀请（只有管理员拉得到）；过期的也在里面，由界面标出来 */
  const invites = ref<InviteDto[]>([])
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

  // ---- 管理员：邀请与成员管理（失败抛 FamilyApiError，由页面提示） ----

  /** 家庭页用：拉不到要让页面知道（load 是给名字显示用的，失败不吭声） */
  async function reloadMembers(): Promise<void> {
    members.value = await unwrap(api.GET('/api/family/members'))
    loaded.value = true
  }

  async function loadInvites(): Promise<void> {
    const all = await unwrap(api.GET('/api/family/invites'))
    invites.value = all.filter((i) => !i.usedBy)
  }

  /** 邀请一个邮箱；这个邮箱已有没用过的邀请时，服务器改的是那一条（角色、有效期按这次的来） */
  async function invite(email: string, role: UserRole): Promise<InviteDto> {
    const created = await unwrap(api.POST('/api/family/invites', { body: { email, role, expiresInDays: 30 } }))
    invites.value = [created, ...invites.value.filter((i) => i.id !== created.id)]
    return created
  }

  async function revokeInvite(id: string): Promise<void> {
    await unwrap(api.DELETE('/api/family/invites/{id}', { params: { path: { id } } }))
    invites.value = invites.value.filter((i) => i.id !== id)
  }

  async function patchMember(id: string, patch: { displayName?: string; role?: UserRole; disabled?: boolean }): Promise<MemberDto> {
    const updated = await unwrap(
      api.PATCH('/api/family/members/{id}', {
        params: { path: { id } },
        body: { displayName: patch.displayName ?? null, role: patch.role ?? null, elderMode: null, disabled: patch.disabled ?? null },
      }),
    )
    members.value = members.value.map((m) => (m.id === id ? updated : m))
    // 改的是自己：左下角头像、"我的"页显示的名字跟着变
    const session = useSessionStore()
    if (session.user?.id === id) session.user = { ...session.user, displayName: updated.displayName, role: updated.role }
    return updated
  }

  return { members, loaded, invites, load, byId, nameOf, titleOf, reloadMembers, loadInvites, invite, revokeInvite, patchMember }
})
