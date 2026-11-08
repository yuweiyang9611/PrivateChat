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
