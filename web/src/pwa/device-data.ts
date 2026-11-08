import type { UserDto } from '@/api/client'
import { resetDb } from '@/db'
import { MEDIA_CACHE } from './media-cache'

// 本机保存的、属于某个账号的数据：身份快照、IndexedDB（消息 / 会话 / 发件箱）、缩略图缓存、tus 续传指纹、待补发的已读。
// 退出登录或换账号时全部清掉，避免家里共用的设备上看到上一个人的消息。

const USER_KEY = 'pc.session.user'
/** 本机 IndexedDB / 缓存里的数据属于哪个用户 */
const OWNER_KEY = 'pc.data.owner'
/** 清库没成功（别的标签页一直占着）时写入：下次任何人登录都先再清一次 */
const DIRTY_OWNER = '__dirty__'
/** 这些是"这个人"的状态，换人后应该重置 */
const PER_USER_KEYS = ['pc.push.dismissed', 'pc.install.dismissed', 'pc.pendingReads']

function storage(): Storage | null {
  try {
    return window.localStorage
  } catch {
    return null
  }
}

/** 上次确认过的登录身份；离线冷启动时先用它进入应用 */
export function readCachedUser(): UserDto | null {
  const raw = storage()?.getItem(USER_KEY)
  if (!raw) return null
  try {
    const u = JSON.parse(raw) as UserDto
    return u && typeof u.id === 'string' ? u : null
  } catch {
    return null
  }
}

export function rememberUser(u: UserDto): void {
  const s = storage()
  if (!s) return
  try {
    s.setItem(USER_KEY, JSON.stringify(u))
    s.setItem(OWNER_KEY, u.id)
  } catch {
    // 配额满 / 隐私模式：只是没法离线冷启动
  }
}

export function forgetUser(): void {
  storage()?.removeItem(USER_KEY)
}

export function dataOwner(): string | null {
  return storage()?.getItem(OWNER_KEY) ?? null
}

/** 关掉通知栏里已经弹出的消息通知（里面有发送者和消息摘要） */
export async function closeNotifications(): Promise<void> {
  try {
    // 不用 serviceWorker.ready：没注册 SW（局域网 http）时它永远不返回
    const reg = await navigator.serviceWorker?.getRegistration()
    for (const n of (await reg?.getNotifications()) ?? []) n.close()
  } catch {
    // ignore
  }
}

/** 清掉本机所有账号数据（界面偏好如暗色、长辈模式保留） */
export async function wipeDeviceData(): Promise<void> {
  // 别的标签页若不响应关闭请求，删库会一直卡住：最多等 3 秒，没删掉就标记，下次登录前再清
  const deleted = await Promise.race([
    resetDb().then(() => true, () => false),
    new Promise<boolean>((resolve) => setTimeout(() => resolve(false), 3_000)),
  ])
  try {
    if ('caches' in window) await caches.delete(MEDIA_CACHE)
  } catch {
    // ignore
  }
  const s = storage()
  if (!s) return
  s.removeItem(USER_KEY)
  if (deleted) s.removeItem(OWNER_KEY)
  else s.setItem(OWNER_KEY, DIRTY_OWNER)
  for (const k of PER_USER_KEYS) s.removeItem(k)
  // tus-js-client 的续传指纹（里面有上传地址）
  for (let i = s.length - 1; i >= 0; i--) {
    const k = s.key(i)
    if (k?.startsWith('tus::')) s.removeItem(k)
  }
}

/** 已安装为 App 时申请持久存储，避免系统空间紧张时清掉消息缓存和发件箱 */
export async function requestPersistentStorage(): Promise<void> {
  try {
    if (!navigator.storage?.persist) return
    if (await navigator.storage.persisted()) return
    // 只在已安装时申请：Chrome 会自动批准，Firefox 普通网页会弹窗询问，长辈容易困惑
    const standalone = window.matchMedia?.('(display-mode: standalone)').matches || ('standalone' in navigator && (navigator as { standalone?: boolean }).standalone === true)
    if (standalone) await navigator.storage.persist()
  } catch {
    // ignore
  }
}

// ---------------- 多标签页同步 ----------------

export type SessionSignal = { type: 'signed-out'; scheme: string } | { type: 'account-switched' }

const CHANNEL = 'pc-session'
const SIGNAL_KEY = 'pc.session.signal'

/** 通知同一浏览器里其它打开的标签页 / 已安装窗口：本机已退出登录或换了账号 */
export function broadcastSessionSignal(signal: SessionSignal): void {
  try {
    if (typeof BroadcastChannel !== 'undefined') {
      const ch = new BroadcastChannel(CHANNEL)
      ch.postMessage(signal)
      ch.close()
      return
    }
  } catch {
    // 退回 storage 事件
  }
  try {
    // 旧 Safari 没有 BroadcastChannel：写 localStorage，其它页面收到 storage 事件
    storage()?.setItem(SIGNAL_KEY, JSON.stringify({ ...signal, at: Date.now() }))
    storage()?.removeItem(SIGNAL_KEY)
  } catch {
    // ignore
  }
}

export function onSessionSignal(cb: (signal: SessionSignal) => void): void {
  if (typeof window === 'undefined') return
  if (typeof BroadcastChannel !== 'undefined') {
    const ch = new BroadcastChannel(CHANNEL)
    ch.onmessage = (e: MessageEvent<SessionSignal>) => cb(e.data)
  }
  window.addEventListener('storage', (e) => {
    if (e.key !== SIGNAL_KEY || !e.newValue) return
    try {
      cb(JSON.parse(e.newValue) as SessionSignal)
    } catch {
      // ignore
    }
  })
}
