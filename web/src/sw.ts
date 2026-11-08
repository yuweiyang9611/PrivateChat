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
}

self.addEventListener('push', (event) => {
  let payload: PushPayload
  try {
    payload = event.data?.json() as PushPayload
  } catch {
    payload = { title: 'PrivateChat', body: event.data?.text() ?? '', url: '/', tag: 'generic' }
  }
  event.waitUntil(
    self.registration.showNotification(payload.title, {
      body: payload.body,
      tag: payload.tag,
      // 安卓通知栏不认 SVG，用 PNG
      icon: '/pwa-192x192.png',
      data: { url: payload.url },
      // 同会话新通知替换旧的并再次提示
      renotify: true,
    } as NotificationOptions),
  )
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const url = (event.notification.data as { url?: string } | undefined)?.url ?? '/'
  event.waitUntil(
    (async () => {
      const all = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
      const target = new URL(url, self.location.origin).href
      for (const c of all) {
        if ('focus' in c) {
          await c.focus()
          if ('navigate' in c && c.url !== target) await c.navigate(target)
          return
        }
      }
      await self.clients.openWindow(target)
    })(),
  )
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
