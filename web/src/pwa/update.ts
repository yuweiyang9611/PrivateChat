import { ref } from 'vue'
import { registerSW } from 'virtual:pwa-register'

/** 新版本已下载、等待切换（或已被别的窗口切换，本页还在跑旧代码） */
export const needRefresh = ref(false)

let updateSW: ((reloadPage?: boolean) => Promise<void>) | null = null
/** 本窗口是否就是点了"立即更新"的那个 */
let applying = false
const CHECK_INTERVAL_MS = 60 * 60 * 1000
const MIN_CHECK_GAP_MS = 10 * 60 * 1000

/**
 * 注册 Service Worker（prompt 模式）：新版本下载完不自动接管，避免用户正在录音、上传时被整页刷新；
 * 底部提示"有新版本"，点了才切换。每小时、以及从后台切回前台时检查一次更新。
 */
export function initPwa(): void {
  if (typeof window === 'undefined' || !('serviceWorker' in navigator)) return
  updateSW = registerSW({
    immediate: true,
    onNeedRefresh() {
      needRefresh.value = true
    },
    // 新 SW 接管了：插件默认会把所有窗口都强制刷新（另一个窗口可能正在录音 / 上传）。
    // 点更新的窗口自己刷新（见 applyUpdate）；其它窗口只亮出提示，等用户自己点
    onNeedReload() {
      if (!applying) needRefresh.value = true
    },
    onRegisteredSW(_url, reg) {
      if (!reg) return
      let lastCheck = Date.now()
      const check = (): void => {
        if (!navigator.onLine || document.visibilityState !== 'visible') return
        if (Date.now() - lastCheck < MIN_CHECK_GAP_MS) return
        lastCheck = Date.now()
        reg.update().catch(() => {
          // 离线或服务器不可达，下次再查
        })
      }
      window.setInterval(() => {
        lastCheck = 0
        check()
      }, CHECK_INTERVAL_MS)
      document.addEventListener('visibilitychange', check)
    },
  })
}

/**
 * 切到新版本并刷新页面。刷新由本页自己做：插件只在"页面加载时已有 SW 控制"的情况下才刷新，
 * 首次访问后一直开着的页面、iOS 主屏 App 首次启动后的会话，点了更新都不会刷新，旧代码继续跑、懒加载分片却已被删。
 */
export async function applyUpdate(): Promise<void> {
  needRefresh.value = false
  applying = true
  const reg = await navigator.serviceWorker.getRegistration()
  if (!reg?.waiting) {
    // 别的窗口已经切过去了：本页还是旧代码，刷新即可
    window.location.reload()
    return
  }
  let reloading = false
  navigator.serviceWorker.addEventListener('controllerchange', () => {
    if (reloading) return
    reloading = true
    window.location.reload()
  })
  await updateSW?.(true)
}
