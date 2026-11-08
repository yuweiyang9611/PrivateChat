import { ref, shallowRef } from 'vue'

/** Chrome / Edge / 安卓浏览器的安装事件（标准里还没有类型） */
interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

function standaloneNow(): boolean {
  if (typeof window === 'undefined') return false
  const iosStandalone = 'standalone' in navigator && (navigator as { standalone?: boolean }).standalone === true
  return iosStandalone || window.matchMedia?.('(display-mode: standalone)').matches === true
}

/** 当前是否以已安装的 App 方式打开（主屏幕图标启动） */
export const isStandalone = ref(standaloneNow())

/** iPhone / iPad（含伪装成 Mac 的 iPadOS Safari）。iOS 只能通过分享菜单"添加到主屏幕"，且装了才能收推送 */
export const isIos =
  typeof navigator !== 'undefined' &&
  (/iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.userAgent.includes('Macintosh') && navigator.maxTouchPoints > 1))

/** 浏览器给的安装机会；为空表示当前不能用按钮直接安装 */
export const installEvent = shallowRef<BeforeInstallPromptEvent | null>(null)

if (typeof window !== 'undefined') {
  window.addEventListener('beforeinstallprompt', (e) => {
    // 不让浏览器自己弹迷你横幅，改由会话列表顶部的提示条和设置页触发
    e.preventDefault()
    installEvent.value = e as BeforeInstallPromptEvent
  })
  window.addEventListener('appinstalled', () => {
    installEvent.value = null
    isStandalone.value = true
  })
  window.matchMedia?.('(display-mode: standalone)').addEventListener?.('change', () => {
    isStandalone.value = standaloneNow()
  })
}

/** 弹出浏览器的安装对话框；返回用户是否同意 */
export async function promptInstall(): Promise<boolean> {
  const e = installEvent.value
  if (!e) return false
  installEvent.value = null // 每个事件只能 prompt 一次
  await e.prompt()
  const choice = await e.userChoice
  return choice.outcome === 'accepted'
}
