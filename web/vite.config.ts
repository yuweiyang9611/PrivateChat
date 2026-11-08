import { fileURLToPath, URL } from 'node:url'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import AutoImport from 'unplugin-auto-import/vite'
import Components from 'unplugin-vue-components/vite'
import { ElementPlusResolver } from 'unplugin-vue-components/resolvers'
import { VitePWA } from 'vite-plugin-pwa'
import { defineConfig } from 'vite'

// 开发期后端地址；生产由 nginx 反代同源路径，无需配置
const API = 'http://localhost:8080'

export default defineConfig({
  plugins: [
    vue(),
    tailwindcss(),
    AutoImport({
      imports: ['vue', 'vue-router', 'pinia', '@vueuse/core'],
      resolvers: [ElementPlusResolver()],
      dts: 'src/auto-imports.d.ts',
    }),
    Components({
      resolvers: [ElementPlusResolver()],
      dts: 'src/components.d.ts',
    }),
    VitePWA({
      // 自定义 SW（src/sw.ts）：预缓存、缩略图缓存、Web Push
      strategies: 'injectManifest',
      srcDir: 'src',
      filename: 'sw.ts',
      // prompt：新版本等用户点"立即更新"再接管，不在录音 / 上传时突然刷新（见 src/pwa/update.ts）
      registerType: 'prompt',
      injectRegister: false,
      // manifest 请求带上 Cookie：Cloudflare Access 后面不带凭据会被重定向，安装提示就出不来
      useCredentials: true,
      devOptions: { enabled: true, type: 'module' },
      // 图标由下面的 globPatterns 统一预缓存，这里不再重复加
      includeManifestIcons: false,
      manifest: {
        id: '/',
        name: 'PrivateChat',
        short_name: '家',
        description: '家庭私有通讯平台',
        lang: 'zh-CN',
        dir: 'ltr',
        display: 'standalone',
        scope: '/',
        start_url: '/',
        theme_color: '#e8873a',
        background_color: '#fdf8f2',
        categories: ['social', 'communication'],
        // 由 public/icon.svg 生成：npm run gen:icons（pwa-assets.config.ts）
        icons: [
          { src: '/pwa-64x64.png', sizes: '64x64', type: 'image/png' },
          { src: '/pwa-192x192.png', sizes: '192x192', type: 'image/png' },
          { src: '/pwa-512x512.png', sizes: '512x512', type: 'image/png', purpose: 'any' },
          { src: '/maskable-icon-512x512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
      injectManifest: {
        // 只预缓存应用壳与静态资源（含所有路由的懒加载分片，离线时每个页面都能打开）
        globPatterns: ['**/*.{js,css,html,svg,png,ico,woff2}'],
      },
    }),
  ],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    proxy: {
      '/api': API,
      '^/s/': API, // 分享链接；不能写 '/s'，否则 /src/* 也会被前缀匹配代理走
      '/openapi': API,
      '/healthz': API,
      '/hubs': { target: API, ws: true },
      '/ws': { target: API, ws: true },
    },
  },
  build: {
    // 带 hash 的产物固定放在 dist/assets，nginx 对 /assets/ 永久缓存
    assetsDir: 'assets',
  },
})
