import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { router } from './router'
import { i18n } from './i18n'
import { initPwa } from './pwa/update'
// 尽早挂上 beforeinstallprompt 监听，事件只在页面加载早期触发一次
import './pwa/install'
import './style.css'
// ElMessage / ElMessageBox 是在脚本里直接调用的，按需引入插件只给模板里的组件带样式，这里手动补上
import 'element-plus/es/components/message/style/css'
import 'element-plus/es/components/message-box/style/css'

initPwa()

const app = createApp(App)
app.use(createPinia())
app.use(router)
app.use(i18n)
app.mount('#app')

// 点系统通知（新消息、未接来电、动态）时应用已经开着：Service Worker 发来要去的地址，
// 用路由跳转而不是整页重新加载，正在进行的通话不会断
navigator.serviceWorker?.addEventListener('message', (e: MessageEvent) => {
  const data = e.data as { type?: string; url?: string } | null
  if (data?.type !== 'open-url' || !data.url) return
  const u = new URL(data.url, window.location.origin)
  if (u.origin === window.location.origin) void router.push(u.pathname + u.search + u.hash)
})
