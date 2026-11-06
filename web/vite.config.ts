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
      // 自定义 SW（src/sw.ts）：预缓存之外还要处理 Web Push
      strategies: 'injectManifest',
      srcDir: 'src',
      filename: 'sw.ts',
      registerType: 'autoUpdate',
      devOptions: { enabled: true, type: 'module' },
      includeAssets: ['icon.svg'],
      manifest: {
        name: 'PrivateChat',
        short_name: '家',
        description: '家庭私有通讯平台',
        lang: 'zh-CN',
        display: 'standalone',
        start_url: '/',
        theme_color: '#e8873a',
        background_color: '#fdf8f2',
        icons: [
          { src: '/icon.svg', sizes: 'any', type: 'image/svg+xml', purpose: 'any' },
          { src: '/icon.svg', sizes: 'any', type: 'image/svg+xml', purpose: 'maskable' },
        ],
      },
      injectManifest: {
        // 仅预缓存应用壳与静态资源；API、媒体、SignalR 的放行规则在 sw.ts 里
        globPatterns: ['**/*.{js,css,html,svg,woff2}'],
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
