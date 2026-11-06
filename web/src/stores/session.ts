import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api, setUnauthorizedHandler, type UserDto } from '@/api/client'
import { startConnection, stopConnection } from '@/realtime/connection'

/**
 * 当前登录用户。
 * 正常路径：身份来自 Cloudflare Access，/api/auth/me 自动登录 / 建号。
 * 备用路径（局域网直连 / 本地开发，没有 Access 头）：/api/auth/me 返回 401，显示密码登录页。
 */
export const useSessionStore = defineStore('session', () => {
  const user = ref<UserDto | null>(null)
  const loading = ref(true)
  const error = ref<string | null>(null)
  /** 已确认未登录（401），需要显示本地登录页 */
  const unauthenticated = ref(false)

  const isAdmin = computed(() => user.value?.role === 'Admin')
  const isChild = computed(() => user.value?.role === 'Child')

  setUnauthorizedHandler(() => {
    user.value = null
    unauthenticated.value = true
    void stopConnection()
  })

  async function load(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      const { data, response } = await api.GET('/api/auth/me')
      if (data) {
        user.value = data
        unauthenticated.value = false
        void startConnection()
      } else if (response.status !== 401) {
        error.value = `HTTP ${response.status}`
      }
    } catch (e) {
      // fetch 抛 TypeError 通常是被跨域重定向到 Access 登录页（CORS 失败），交给 reload
      error.value = e instanceof Error ? e.message : String(e)
    } finally {
      loading.value = false
    }
  }

  async function localLogin(usernameOrEmail: string, password: string): Promise<string | null> {
    const { data, error: err, response } = await api.POST('/api/auth/local/login', {
      body: { usernameOrEmail, password },
    })
    if (data) {
      user.value = data
      unauthenticated.value = false
      void startConnection()
      return null
    }
    return err?.title ?? `HTTP ${response.status}`
  }

  async function logout(): Promise<void> {
    await api.POST('/api/auth/local/logout')
    user.value = null
    unauthenticated.value = true
    await stopConnection()
  }

  return { user, loading, error, unauthenticated, isAdmin, isChild, load, localLogin, logout }
})
