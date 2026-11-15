// 会话入口的判定：/api/auth/me 的回答决定显示应用、"不是家庭成员"说明页，还是局域网密码登录页。
// 真实的 session store + api 客户端（fetch 用可控的假实现）+ Dexie（fake-indexeddb）；SignalR / 推送用假实现。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory, IDBKeyRange } from 'fake-indexeddb'
import Dexie from 'dexie'

const ctl = vi.hoisted(() => ({
  /** /api/auth/me 的回答，按顺序用，用完重复最后一个 */
  me: [] as (() => Response)[],
  meCalls: 0,
  connectionStarts: 0,
}))

vi.mock('@/realtime/connection', async () => {
  const { ref } = await import('vue')
  return {
    connectionState: ref(0),
    startConnection: async () => {
      ctl.connectionStarts++
    },
    stopConnection: async () => undefined,
  }
})

vi.mock('@/push', () => ({ disablePush: async () => undefined }))

const MOM = {
  id: '00000000-0000-7000-8000-00000000000a', email: 'mom@example.com', username: 'mom', displayName: '妈妈', role: 'Member',
  elderMode: false, avatarBlobId: null, authScheme: 'CloudflareAccess',
}

const ok = (user: unknown) => () => new Response(JSON.stringify(user), { status: 200, headers: { 'Content-Type': 'application/json' } })
/** 经 Cloudflare Access 进来被拒：401 + 原因（后端 CloudflareAccessAuthenticationHandler） */
const rejected = (body: unknown) => () => new Response(JSON.stringify(body), { status: 401, headers: { 'Content-Type': 'application/problem+json' } })
/** 局域网 / 本地模式没登录：401，没有正文 */
const bare401 = () => new Response(null, { status: 401 })

/** 模拟一次"打开页面"：模块、Pinia 全新，localStorage / IndexedDB 保留 */
async function boot() {
  vi.resetModules()
  const { createPinia, setActivePinia } = await import('pinia')
  setActivePinia(createPinia())
  const { useSessionStore } = await import('@/stores/session')
  return useSessionStore()
}

const settle = () => new Promise((r) => setTimeout(r, 20))

beforeEach(() => {
  const factory = new IDBFactory()
  globalThis.indexedDB = factory
  Dexie.dependencies.indexedDB = factory
  Dexie.dependencies.IDBKeyRange = IDBKeyRange
  Object.assign(ctl, { me: [], meCalls: 0, connectionStarts: 0 })
  localStorage.clear()
  sessionStorage.clear()
  vi.spyOn(navigator, 'onLine', 'get').mockImplementation(() => true)
  vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url
    if (!url.endsWith('/api/auth/me')) throw new TypeError('Failed to fetch')
    const answer = ctl.me[Math.min(ctl.meCalls++, ctl.me.length - 1)]!
    return answer()
  })
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('过了 Cloudflare Access 但邮箱不是家庭成员', () => {
  it('显示说明页并带上邮箱，不落到密码登录页', async () => {
    ctl.me = [rejected({ title: '该邮箱不在家庭成员或邀请列表中', status: 401, code: 'access_not_member', email: 'stranger@example.com' })]
    const session = await boot()

    await session.load()

    expect(session.loading).toBe(false)
    expect(session.user).toBeNull()
    expect(session.accessDenied).toEqual({ reason: 'not-member', email: 'stranger@example.com' })
    expect(ctl.connectionStarts).toBe(0)
  })

  it('管理员邀请之后点"再试一次"就进来了', async () => {
    ctl.me = [rejected({ code: 'access_not_member', email: 'mom@example.com' }), ok(MOM)]
    const session = await boot()
    await session.load()
    expect(session.accessDenied?.reason).toBe('not-member')

    await session.load()

    expect(session.accessDenied).toBeNull()
    expect(session.user?.email).toBe('mom@example.com')
    expect(ctl.connectionStarts).toBe(1)
  })

  it('用着用着被停用：下次打开直接看到说明页，不去 Access 兜圈子，也不显示密码登录页', async () => {
    ctl.me = [ok(MOM)]
    await (await boot()).load()

    ctl.meCalls = 0
    ctl.me = [rejected({ code: 'access_not_member', email: 'mom@example.com' })]
    const session = await boot()
    await session.load() // 有缓存身份：先进应用，后台再确认
    expect(session.user?.email).toBe('mom@example.com')
    await settle()

    expect(session.user).toBeNull()
    expect(session.accessDenied).toEqual({ reason: 'not-member', email: 'mom@example.com' })
    // reauthenticate() 会记下跳转时间：没记就是没跳
    expect(sessionStorage.getItem('pc.reauthAt')).toBeNull()
    // 缓存的身份也忘掉：下次打开不会先闪一下应用界面
    const { readCachedUser } = await import('@/pwa/device-data')
    expect(readCachedUser()).toBeNull()
  })

  it('服务器校验不了 Access 身份：也是说明页（原因不同）', async () => {
    ctl.me = [rejected({ code: 'access_invalid' })]
    const session = await boot()

    await session.load()

    expect(session.accessDenied).toEqual({ reason: 'invalid', email: null })
    expect(session.user).toBeNull()
  })
})

describe('局域网 / 本地模式', () => {
  it('没有正文的 401：显示密码登录页', async () => {
    ctl.me = [bare401]
    const session = await boot()

    await session.load()

    expect(session.loading).toBe(false)
    expect(session.user).toBeNull()
    expect(session.unauthenticated).toBe(true)
    expect(session.accessDenied).toBeNull()
  })
})
