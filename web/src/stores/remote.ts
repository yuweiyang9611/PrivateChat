import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ElNotification } from 'element-plus'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { useSessionStore } from './session'
import { useFamilyStore } from './family'

export type DeviceDto = components['schemas']['DeviceDto']
export type RemoteSessionDto = components['schemas']['RemoteSessionDto']
export type RemoteSessionMode = components['schemas']['RemoteSessionMode']
export type AgentSetupDto = components['schemas']['AgentSetupDto']

export interface DeviceChangedEvent { deviceId: string }

/** 接口错误：带服务器的说明（ProblemDetails.detail / title） */
export class RemoteApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

type Result<T> = { data?: T; error?: unknown; response: Response }

export async function unwrap<T>(request: Promise<Result<T>>): Promise<T> {
  let r: Result<T>
  try {
    r = await request
  } catch {
    throw new RemoteApiError(0, '网络不通')
  }
  if (!r.response.ok) {
    const problem = r.error as { title?: string; detail?: string | null } | undefined
    throw new RemoteApiError(r.response.status, problem?.detail || problem?.title || `HTTP ${r.response.status}`)
  }
  return r.data as T
}

/**
 * 远程控制：设备列表（装了 Agent 的电脑）与会话。
 * SignalR：DeviceChanged（上下线、改名、改策略）合并 300 ms 后重新拉列表；RemoteSessionChanged 直接更新会话。
 */
export const useRemoteStore = defineStore('remote', () => {
  const devices = ref<DeviceDto[]>([])
  const loaded = ref(false)
  /** 最近见过的会话（按 Id），远程窗口据此跟随状态变化 */
  const sessions = ref<Record<string, RemoteSessionDto>>({})
  let loading: Promise<void> | null = null
  /** 拉列表期间又有变化：拉完再拉一次（正在进行的那次拿到的可能是变化之前的） */
  let dirty = false
  let reloadTimer: number | undefined
  const notified = new Set<string>()

  function load(): Promise<void> {
    if (loading) {
      dirty = true
      return loading
    }
    loading = (async () => {
      try {
        do {
          dirty = false
          devices.value = await unwrap(api.GET('/api/devices'))
          loaded.value = true
        } while (dirty)
      } finally {
        loading = null
      }
    })()
    return loading
  }

  function scheduleReload(): void {
    if (!loaded.value && !loading) return
    window.clearTimeout(reloadTimer)
    reloadTimer = window.setTimeout(() => void load().catch(() => undefined), 300)
  }

  const rank = { Pending: 0, Active: 1, Ended: 2 } as const

  function remember(s: RemoteSessionDto): void {
    const known = sessions.value[s.id]
    // 迟到的旧状态不覆盖新的：状态只会往前走（等同意 → 进行中 → 结束）
    if (known && rank[s.state] < rank[known.state]) return
    sessions.value = { ...sessions.value, [s.id]: s }
    const i = devices.value.findIndex((d) => d.id === s.targetDeviceId)
    if (i >= 0) {
      const d = devices.value[i]!
      const session = s.endedAt ? (d.session?.id === s.id ? null : d.session) : s
      devices.value = devices.value.map((x, j) => (j === i ? { ...d, session } : x))
    }
  }

  function onSessionChanged(s: RemoteSessionDto): void {
    remember(s)
    // 我的电脑正在被别人远程：提醒一下（网页开着时；电脑上也有红条）
    const me = useSessionStore().user?.id
    if (me && s.deviceOwnerId === me && s.controllerUserId !== me && s.state === 'Active' && !notified.has(s.id)) {
      notified.add(s.id)
      const who = useFamilyStore().nameOf(s.controllerUserId)
      ElNotification({ title: '远程协助', message: `${who} 正在${s.mode === 'View' ? '查看' : '控制'}你的电脑「${s.deviceName}」`, type: 'warning' })
    }
    scheduleReload()
  }

  function onDeviceChanged(): void {
    scheduleReload()
  }

  async function start(deviceId: string, mode: RemoteSessionMode, pin?: string): Promise<RemoteSessionDto> {
    const s = await unwrap(api.POST('/api/remote/sessions', { body: { targetDeviceId: deviceId, mode, pin: pin || null } }))
    remember(s)
    return s
  }

  async function get(id: string): Promise<RemoteSessionDto> {
    const s = await unwrap(api.GET('/api/remote/sessions/{id}', { params: { path: { id } } }))
    remember(s)
    return s
  }

  async function end(id: string): Promise<void> {
    const s = await unwrap(api.POST('/api/remote/sessions/{id}/end', { params: { path: { id } } }))
    remember(s)
  }

  async function update(id: string, body: components['schemas']['UpdateDeviceRequest']): Promise<void> {
    const d = await unwrap(api.PATCH('/api/devices/{id}', { params: { path: { id } }, body }))
    devices.value = devices.value.map((x) => (x.id === id ? d : x))
  }

  async function revoke(id: string): Promise<void> {
    await unwrap(api.DELETE('/api/devices/{id}', { params: { path: { id } } }))
    devices.value = devices.value.filter((x) => x.id !== id)
  }

  /** 重连后：进行中的会话可能在断线期间结束了 */
  function resync(): void {
    scheduleReload()
    for (const s of Object.values(sessions.value)) if (!s.endedAt) void get(s.id).catch(() => undefined)
  }

  return { devices, loaded, sessions, load, onSessionChanged, onDeviceChanged, start, get, end, update, revoke, resync }
})
