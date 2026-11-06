/// <reference lib="webworker" />
// 自定义 Service Worker（vite-plugin-pwa injectManifest）：应用壳预缓存 + Web Push 通知。
import { clientsClaim } from 'workbox-core'
import { cleanupOutdatedCaches, createHandlerBoundToURL, precacheAndRoute } from 'workbox-precaching'
import { NavigationRoute, registerRoute } from 'workbox-routing'

declare let self: ServiceWorkerGlobalScope

self.skipWaiting()
clientsClaim()

precacheAndRoute(self.__WB_MANIFEST)
cleanupOutdatedCaches()

// SPA 回退：API、SignalR、分享链接、媒体一律走网络
registerRoute(
  new NavigationRoute(createHandlerBoundToURL('index.html'), {
    denylist: [/^\/api\//, /^\/hubs\//, /^\/ws\//, /^\/s\//, /^\/openapi\//, /^\/healthz/, /^\/scalar/],
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
      icon: '/icon.svg',
      badge: '/icon.svg',
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
