import { ref } from 'vue'
import { api } from '@/api/client'

/** 浏览器是否具备 Web Push 条件（HTTPS / localhost，且有 SW + PushManager + Notification） */
export function pushSupported(): boolean {
  return window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window
}

export type PushState = 'unsupported' | 'default' | 'denied' | 'subscribed' | 'unsubscribed'

export const pushState = ref<PushState>(pushSupported() ? (Notification.permission === 'denied' ? 'denied' : 'default') : 'unsupported')

/** 用户明确关掉过通知时不再提示 */
const DISMISS_KEY = 'pc.push.dismissed'
export function pushDismissed(): boolean {
  try { return localStorage.getItem(DISMISS_KEY) === '1' } catch { return false }
}
export function dismissPush(): void {
  try { localStorage.setItem(DISMISS_KEY, '1') } catch { /* 隐私模式 */ }
}

function urlBase64ToUint8Array(base64: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (base64.length % 4)) % 4)
  const raw = atob((base64 + padding).replace(/-/g, '+').replace(/_/g, '/'))
  const out = new Uint8Array(new ArrayBuffer(raw.length))
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i)
  return out
}

async function registration(): Promise<ServiceWorkerRegistration> {
  return await navigator.serviceWorker.ready
}

/** 启动时：已经订阅过就把订阅再登记一次（服务端可能重装过），并刷新状态 */
export async function refreshPushState(): Promise<void> {
  if (!pushSupported()) { pushState.value = 'unsupported'; return }
  if (Notification.permission === 'denied') { pushState.value = 'denied'; return }
  try {
    const reg = await registration()
    const sub = await reg.pushManager.getSubscription()
    if (sub) {
      pushState.value = 'subscribed'
      // 重新登记失败（离线）不影响状态，下次启动再登记
      await api.POST('/api/push/subscribe', { body: toRequest(sub) }).catch(() => undefined)
    } else {
      pushState.value = Notification.permission === 'granted' ? 'unsubscribed' : 'default'
    }
  } catch {
    pushState.value = 'default'
  }
}

/** 必须在用户手势里调用（权限弹窗） */
export async function enablePush(): Promise<boolean> {
  if (!pushSupported()) return false
  const permission = await Notification.requestPermission()
  if (permission !== 'granted') {
    pushState.value = permission === 'denied' ? 'denied' : 'default'
    return false
  }
  const { data } = await api.GET('/api/push/vapid-public-key')
  if (!data) return false
  const reg = await registration()
  const sub =
    (await reg.pushManager.getSubscription()) ??
    (await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: urlBase64ToUint8Array(data.publicKey) }))
  const res = await api.POST('/api/push/subscribe', { body: toRequest(sub) })
  pushState.value = res.data ? 'subscribed' : 'default'
  return !!res.data
}

/**
 * 注销本机推送。先在浏览器侧退订（离线也能生效，旧 endpoint 随即作废，服务器下次推送收到 410 会自动清理），
 * 再尽量通知服务器。不用 serviceWorker.ready：没注册 SW 时它永远不会返回，退出登录会卡住。
 */
export async function disablePush(): Promise<void> {
  if (!pushSupported()) return
  const reg = await navigator.serviceWorker.getRegistration()
  const sub = await reg?.pushManager.getSubscription()
  if (sub) {
    const endpoint = sub.endpoint
    try {
      await sub.unsubscribe()
    } finally {
      try {
        await api.DELETE('/api/push/subscribe', { body: { endpoint } })
      } catch {
        // 离线：服务器那边等推送失败时清理
      }
    }
  }
  pushState.value = 'unsubscribed'
}

export async function sendTestPush(): Promise<boolean> {
  const res = await api.POST('/api/push/test')
  return res.response.ok
}

function toRequest(sub: PushSubscription): { endpoint: string; keys: { p256dh: string; auth: string }; userAgent: string } {
  const json = sub.toJSON()
  return {
    endpoint: json.endpoint ?? sub.endpoint,
    keys: { p256dh: json.keys?.p256dh ?? '', auth: json.keys?.auth ?? '' },
    userAgent: navigator.userAgent,
  }
}
