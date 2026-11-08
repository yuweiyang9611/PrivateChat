import { ref } from 'vue'

/**
 * 浏览器报告的联网状态。navigator.onLine 为 false 时基本可信（飞行模式、断 Wi-Fi）；
 * 为 true 不代表服务器可达，服务器是否可达看 SignalR 连接状态。
 */
export const online = ref(typeof navigator === 'undefined' ? true : navigator.onLine)

const listeners = new Set<() => void>()

if (typeof window !== 'undefined') {
  window.addEventListener('online', () => {
    online.value = true
    for (const cb of listeners) cb()
  })
  window.addEventListener('offline', () => {
    online.value = false
  })
}

/** 从离线恢复联网时回调；返回取消函数 */
export function onOnline(cb: () => void): () => void {
  listeners.add(cb)
  return () => listeners.delete(cb)
}

export function isOnline(): boolean {
  return typeof navigator === 'undefined' ? true : navigator.onLine
}
