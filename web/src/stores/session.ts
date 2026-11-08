import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import { HubConnectionState } from '@microsoft/signalr'
import { api, fetchMe, reauthenticate, setUnauthorizedHandler, type UserDto } from '@/api/client'
import { connectionState, startConnection, stopConnection } from '@/realtime/connection'
import { ensureDbOpen, onDbDeletedElsewhere } from '@/db'
import { isOnline } from '@/net/online'
import {
  broadcastSessionSignal,
  closeNotifications,
  dataOwner,
  forgetUser,
  onSessionSignal,
  readCachedUser,
  rememberUser,
  requestPersistentStorage,
  wipeDeviceData,
} from '@/pwa/device-data'
import { disablePush } from '@/push'

const ACCESS = 'CloudflareAccess'

export type LogoutResult = 'ok' | 'offline' | 'failed'

/**
 * 当前登录用户。
 * 正常路径：身份来自 Cloudflare Access，/api/auth/me 自动登录 / 建号。
 * 备用路径（局域网直连 / 本地开发，没有 Access 头）：/api/auth/me 返回 401，显示密码登录页。
 * 离线冷启动：先用上次确认过的身份进入应用（读本地缓存），联网后再向服务器确认。
 */
export const useSessionStore = defineStore('session', () => {
  const user = ref<UserDto | null>(null)
  const loading = ref(true)
  const error = ref<string | null>(null)
  /** 已确认未登录（401），需要显示本地登录页 */
  const unauthenticated = ref(false)
  /** 当前身份来自本地缓存，还没和服务器确认过（离线启动） */
  const unverified = ref(false)
  /** 本页上一次确认过的用户：同一页里换人登录时，内存里还留着上一位的消息 */
  let lastAccepted: string | null = null

  const isAdmin = computed(() => user.value?.role === 'Admin')
  const isChild = computed(() => user.value?.role === 'Child')

  setUnauthorizedHandler(() => {
    // Access 模式下 401 说明 Access 会话没带上：去边缘重新验证，而不是显示密码登录页
    if (user.value?.authScheme === ACCESS && reauthenticate()) return
    signedOut()
  })

  function signedOut(): void {
    user.value = null
    unauthenticated.value = true
    unverified.value = false
    forgetUser()
    void stopConnection()
  }

  // ---- 其它标签页 / 已安装窗口退出登录或换了账号 ----
  onSessionSignal((signal) => {
    void stopConnection()
    // Access 模式：本页也去结束 Access 会话；直接刷新可能赶在 Access 退出之前，又被自动登录回上一个人
    if (signal.type === 'signed-out' && signal.scheme === ACCESS) window.location.assign('/cdn-cgi/access/logout')
    else window.location.reload()
  })
  // 别的页面在删库：本页的库已被关闭且不会自动重开；断开连接，等上面的广播决定刷新还是退出
  onDbDeletedElsewhere(() => {
    void stopConnection()
  })

  /** 本机数据属于别人（或上次没清干净）：先清掉再以新账号重新进入 */
  async function switchAccount(u: UserDto): Promise<void> {
    await stopConnection()
    await closeNotifications()
    // 浏览器侧退订：订阅是按浏览器、不按人的，不退的话上一位的消息还会推到这台设备上
    await disablePush().catch(() => undefined)
    await wipeDeviceData()
    rememberUser(u)
    broadcastSessionSignal({ type: 'account-switched' })
    window.location.reload()
  }

  /** 服务器确认了身份 */
  async function accept(u: UserDto): Promise<void> {
    const previous = dataOwner() ?? lastAccepted ?? user.value?.id ?? null
    if (previous && previous !== u.id) {
      await switchAccount(u)
      return
    }
    await ensureDbOpen()
    rememberUser(u)
    lastAccepted = u.id
    user.value = u
    unauthenticated.value = false
    unverified.value = false
    error.value = null
    void startConnection()
    void requestPersistentStorage()
  }

  /**
   * 启动：有缓存身份就立即进入应用（不等网络），后台再确认；没有缓存就等服务器回答。
   */
  async function load(): Promise<void> {
    loading.value = true
    error.value = null
    const cached = readCachedUser()
    if (cached) {
      user.value = cached
      unverified.value = true
      loading.value = false
      void verify(cached)
      return
    }
    await verify(null)
    loading.value = false
  }

  async function verify(cached: UserDto | null): Promise<void> {
    const { probe, user: fresh } = await fetchMe()
    switch (probe) {
      case 'ok':
        await accept(fresh!)
        break
      case 'unauthorized':
        if (cached?.authScheme === ACCESS && reauthenticate()) break
        signedOut()
        break
      case 'access-expired':
        if (reauthenticate()) break
        // 60 秒冷却期内不重复跳：有缓存身份就先离线用着，连接层按退避重试，冷却过后会再去重新验证
        if (cached) void startConnection()
        else error.value = 'access-expired'
        break
      default:
        // offline / unreachable：有缓存身份就离线使用，连接层会在联网 / 服务器恢复后自己重连
        if (cached) void startConnection()
        else error.value = probe
    }
  }

  // 离线启动后连上了服务器：顺手确认一次身份（拿到最新资料，或发现会话已失效）
  watch(connectionState, (s) => {
    if (s === HubConnectionState.Connected && unverified.value && user.value) void verify(user.value)
  })

  async function localLogin(usernameOrEmail: string, password: string): Promise<string | null> {
    try {
      const { data, error: err, response } = await api.POST('/api/auth/local/login', {
        body: { usernameOrEmail, password },
      })
      if (data) {
        await accept(data)
        return null
      }
      return err?.title ?? `HTTP ${response.status}`
    } catch {
      return 'offline'
    }
  }

  /**
   * 退出登录：必须能联系到服务器才执行——否则本机数据清了，登录凭据（30 天 Cookie / Access 会话）却还有效，
   * 下次一联网又自动登录回来，交给家里别人用的设备就等于没退出。
   * 顺序：确认服务器可达 → 断开连接、关掉已弹出的通知、退订本机推送 → 服务器端注销 → 清本机数据 → 通知其它标签页。
   * Access 模式最后去 Cloudflare 结束 Access 会话。
   */
  async function logout(): Promise<LogoutResult> {
    const scheme = user.value?.authScheme
    if (!isOnline()) return 'offline'
    const { probe } = await fetchMe()
    if (probe === 'offline' || probe === 'unreachable') return 'offline'
    // probe 为 unauthorized / access-expired：服务器端会话本来就没了，直接清本机即可

    await stopConnection()
    await closeNotifications()
    await disablePush().catch(() => undefined)
    if (scheme !== ACCESS && probe === 'ok') {
      const res = await api.POST('/api/auth/local/logout').catch(() => null)
      if (!res || !(res.response.ok || res.response.status === 401)) {
        // 服务器没注销成功：不清本机数据，恢复连接，让用户稍后再试
        void startConnection()
        return 'failed'
      }
    }
    await wipeDeviceData()
    broadcastSessionSignal({ type: 'signed-out', scheme: scheme ?? '' })
    user.value = null
    if (scheme === ACCESS) window.location.assign('/cdn-cgi/access/logout')
    else window.location.replace('/')
    return 'ok'
  }

  return { user, loading, error, unauthenticated, unverified, isAdmin, isChild, load, localLogin, logout }
})
