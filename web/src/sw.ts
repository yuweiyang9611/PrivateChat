/// <reference lib="webworker" />
// 自定义 Service Worker（vite-plugin-pwa injectManifest，prompt 模式）：
// 应用壳预缓存（离线可开）、缩略图运行时缓存、Web Push 通知。
import { clientsClaim } from 'workbox-core'
import { cleanupOutdatedCaches, createHandlerBoundToURL, precacheAndRoute } from 'workbox-precaching'
import { NavigationRoute, registerRoute } from 'workbox-routing'
import { CacheFirst } from 'workbox-strategies'
import { ExpirationPlugin } from 'workbox-expiration'
import { MEDIA_CACHE, isThumbRequest, mediaCacheKey, shouldCacheMedia } from './pwa/media-cache'

declare let self: ServiceWorkerGlobalScope

// prompt 模式：新版本装好后等待，用户点"立即更新"时页面发 SKIP_WAITING 再接管
self.addEventListener('message', (event) => {
  if ((event.data as { type?: string } | null)?.type === 'SKIP_WAITING') void self.skipWaiting()
})
clientsClaim()

precacheAndRoute(self.__WB_MANIFEST)
cleanupOutdatedCaches()

// SPA 回退：页面导航一律给缓存里的 index.html（离线可开）。
// 以下路径必须走网络：API、SignalR、分享链接、OpenAPI 文档、Cloudflare 的 /cdn-cgi/（Access 登录 / 退出），以及 /downloads/（远程协助程序安装包）。
// 它们也不是应用页面（新标签页里打开的 PDF、外链分享页）：通知点开时不能把它们当成应用窗口。
const NON_APP_PATHS = [/^\/api\//, /^\/hubs\//, /^\/ws\//, /^\/s\//, /^\/openapi\//, /^\/healthz/, /^\/scalar/, /^\/cdn-cgi\//, /^\/downloads\//]
registerRoute(new NavigationRoute(createHandlerBoundToURL('index.html'), { denylist: NON_APP_PATHS }))

// 缩略图：内容寻址、生成后不变，缓存优先；离线时之前看过的图照样能显示
registerRoute(
  ({ url, request }) => request.method === 'GET' && isThumbRequest(url, self.location.origin),
  new CacheFirst({
    cacheName: MEDIA_CACHE,
    plugins: [
      { cacheKeyWillBeUsed: async ({ request }) => mediaCacheKey(request.url) },
      { cacheWillUpdate: async ({ response }) => (shouldCacheMedia(response.status, response.headers.get('Cache-Control')) ? response : null) },
      new ExpirationPlugin({ maxEntries: 1000, maxAgeSeconds: 60 * 24 * 3600, purgeOnQuotaError: true }),
    ],
  }),
)

interface PushPayload {
  title: string
  body: string
  url: string
  tag: string
  count?: number
  /** message 新消息；moment 动态被评论 / 点赞；call 来电；call-missed 未接来电；call-answered 已在别的设备接听 */
  kind?: 'message' | 'moment' | 'call' | 'call-missed' | 'call-answered'
  callId?: string | null
}

interface NotificationData { url?: string; kind?: string; callId?: string | null }

// 安卓通知栏不认 SVG，用 PNG
const ICON = '/pwa-192x192.png'

/** 运行着应用的窗口（同源的 PDF 标签页、外链分享页没有应用的消息监听，不算） */
async function windows(): Promise<WindowClient[]> {
  const all = (await self.clients.matchAll({ type: 'window', includeUncontrolled: true })) as WindowClient[]
  return all.filter((c) => !NON_APP_PATHS.some((r) => r.test(new URL(c.url).pathname)))
}

/**
 * 推送必须对应一条用户看得见的通知：iOS 上推送事件里没显示过通知会被记一次"静默推送"，几次之后整个订阅被收回
 * （之后消息、来电都收不到）；Chrome 则在推送事件结束时数一数还显示着的通知，一条都没有（且没有可见的页面）
 * 就自己弹"网站已在后台更新"。所以：
 * - 页面在前台（可见，Chrome 豁免）时：显示一下马上关掉（满足 iOS）；
 * - 否则：显示一条安静的说明（同一个 tag，顺带替换掉还挂着的响铃），推送事件结束后过几秒再关掉。
 */
async function showQuietly(payload: PushPayload, data: NotificationData, focused: boolean): Promise<void> {
  await self.registration.showNotification(payload.title, { body: payload.body, tag: payload.tag, icon: ICON, data, silent: true, renotify: false } as NotificationOptions)
  if (focused) {
    for (const n of await self.registration.getNotifications({ tag: payload.tag })) n.close()
  } else {
    closeLater(payload.tag)
  }
}

/** 不放进 waitUntil：推送事件结束时这条还显示着；Service Worker 被回收得更早的话，它就留在通知栏里，也无妨 */
function closeLater(tag: string, ms = 5000): void {
  setTimeout(() => {
    void self.registration.getNotifications({ tag }).then((list) => list.forEach((n) => n.close()))
  }, ms)
}

async function handlePush(payload: PushPayload): Promise<void> {
  const kind = payload.kind ?? 'message'
  const data: NotificationData = { url: payload.url, kind, callId: payload.callId }
  if (kind === 'message' || kind === 'moment') {
    await self.registration.showNotification(payload.title, {
      body: payload.body,
      tag: payload.tag,
      icon: ICON,
      data,
      // 同会话新通知替换旧的并再次提示
      renotify: true,
    } as NotificationOptions)
    return
  }

  // 来电一类：页面正开在前台时，页面自己弹来电窗、响铃，这里不再重复弹系统通知
  const focused = (await windows()).some((c) => c.focused)

  if (kind === 'call') {
    // 页面在前台：页面自己弹来电窗、响铃
    if (focused) {
      await showQuietly(payload, data, true)
      return
    }
    await self.registration.showNotification(payload.title, {
      body: payload.body,
      tag: payload.tag,
      icon: ICON,
      data,
      renotify: true,
      // 一直留到接听 / 拒绝 / 被"未接来电"替换（iOS 不支持按钮和常驻，点通知打开应用后在页面里接）
      requireInteraction: true,
      vibrate: [400, 200, 400, 200, 400],
      actions: [
        { action: 'answer', title: '接听' },
        { action: 'decline', title: '拒绝' },
      ],
    } as NotificationOptions)
    return
  }

  if (kind === 'call-answered') {
    // 在别的设备上接了 / 通话已结束 / 已拒接：把这台设备上还挂着的响铃通知原地换成一句说明，稍后自动关掉
    await showQuietly(payload, data, focused)
    return
  }

  // call-missed：把响铃通知原地换成"未接来电"（同一个 tag），不再响；页面在前台时页面里已经看得到
  if (focused) {
    await showQuietly(payload, data, true)
    return
  }
  await self.registration.showNotification(payload.title, { body: payload.body, tag: payload.tag, icon: ICON, data, renotify: false } as NotificationOptions)
}

self.addEventListener('push', (event) => {
  let payload: PushPayload
  try {
    payload = event.data?.json() as PushPayload
  } catch {
    payload = { title: 'PrivateChat', body: event.data?.text() ?? '', url: '/', tag: 'generic' }
  }
  event.waitUntil(handlePush(payload))
})

/**
 * 打开（或切到）应用并跳到 url。应用已经开着时交给页面用路由跳转，而不是整页重新加载：
 * 通话中点开一条消息 / 未接来电通知，正在进行的通话不会因为页面重载而断掉。
 */
async function openUrl(url: string): Promise<void> {
  const target = new URL(url, self.location.origin).href
  const open = await windows()
  const client = open.find((c) => c.focused) ?? open[0]
  if (client) {
    await client.focus().catch(() => undefined)
    client.postMessage({ type: 'open-url', url: target })
    return
  }
  await self.clients.openWindow(target)
}

/** 来电通知：应用开着就把动作转给页面（不重新加载，正在进行的通话不会断）；没开就带着参数打开 */
async function openCall(data: NotificationData, action: 'answer' | undefined): Promise<void> {
  const open = await windows()
  const client = open.find((c) => c.focused) ?? open[0]
  if (client) {
    await client.focus().catch(() => undefined)
    client.postMessage({ type: 'call-action', callId: data.callId, action })
    return
  }
  const target = new URL(data.url ?? '/', self.location.origin)
  if (data.callId && !target.searchParams.has('call')) target.searchParams.set('call', data.callId)
  if (action) target.searchParams.set('action', action)
  await self.clients.openWindow(target.href)
}

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const data = (event.notification.data as NotificationData | undefined) ?? {}
  if (data.kind === 'call' && data.callId) {
    if (event.action === 'decline') {
      event.waitUntil(fetch(`/api/calls/${data.callId}/decline`, { method: 'POST', credentials: 'include' }).then(() => undefined, () => undefined))
      return
    }
    // 点"接听"直接接；点通知正文只是打开应用，在页面里的来电窗上决定
    event.waitUntil(openCall(data, event.action === 'answer' ? 'answer' : undefined))
    return
  }
  event.waitUntil(openUrl(data.url ?? '/'))
})
// 浏览器轮换订阅时重新登记
self.addEventListener('pushsubscriptionchange', (event) => {
  const e = event as ExtendableEvent & { oldSubscription?: PushSubscription | null; newSubscription?: PushSubscription | null }
  e.waitUntil(
    (async () => {
      const sub = e.newSubscription ?? (await self.registration.pushManager.getSubscription())
      if (!sub) return
      await fetch('/api/push/subscribe', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(sub.toJSON()),
      })
    })(),
  )
})
