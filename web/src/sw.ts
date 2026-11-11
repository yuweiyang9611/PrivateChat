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
// 以下路径必须走网络：API、SignalR、分享链接、OpenAPI 文档，以及 Cloudflare 的 /cdn-cgi/（Access 登录 / 退出）。
registerRoute(
  new NavigationRoute(createHandlerBoundToURL('index.html'), {
    denylist: [/^\/api\//, /^\/hubs\//, /^\/ws\//, /^\/s\//, /^\/openapi\//, /^\/healthz/, /^\/scalar/, /^\/cdn-cgi\//],
  }),
)

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
  /** message 新消息；call 来电；call-missed 未接来电；call-answered 已在别的设备接听 */
  kind?: 'message' | 'call' | 'call-missed' | 'call-answered'
  callId?: string | null
}

interface NotificationData { url?: string; kind?: string; callId?: string | null }

// 安卓通知栏不认 SVG，用 PNG
const ICON = '/pwa-192x192.png'

async function windows(): Promise<WindowClient[]> {
  return (await self.clients.matchAll({ type: 'window', includeUncontrolled: true })) as WindowClient[]
}

async function handlePush(payload: PushPayload): Promise<void> {
  const kind = payload.kind ?? 'message'
  const data: NotificationData = { url: payload.url, kind, callId: payload.callId }
  if (kind === 'message') {
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
  const ringing = await self.registration.getNotifications({ tag: payload.tag })

  if (kind === 'call') {
    if (focused) return
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
    // 在别的设备上接了：把这台设备上还挂着的响铃通知收掉。没有挂着的就什么也不做
    if (!ringing.length) return
    if (focused) {
      for (const n of ringing) n.close()
      return
    }
    // 推送必须对应一条可见通知：先原地替换成一句说明，稍后自动关掉
    await self.registration.showNotification(payload.title, { body: payload.body, tag: payload.tag, icon: ICON, data, renotify: false, silent: true } as NotificationOptions)
    await new Promise((resolve) => setTimeout(resolve, 2500))
    for (const n of await self.registration.getNotifications({ tag: payload.tag })) n.close()
    return
  }

  // call-missed：把响铃通知原地换成"未接来电"（同一个 tag），不再响
  if (focused) {
    for (const n of ringing) n.close()
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

/** 打开（或切到）应用并导航到 url */
async function openUrl(url: string): Promise<void> {
  const target = new URL(url, self.location.origin).href
  for (const c of await windows()) {
    if ('focus' in c) {
      await c.focus()
      if ('navigate' in c && c.url !== target) await c.navigate(target)
      return
    }
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
