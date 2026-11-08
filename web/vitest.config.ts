import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vitest/config'

// 单测不加载 PWA / 自动导入等插件，只做路径别名；IndexedDB 用 fake-indexeddb 模拟
export default defineConfig({
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['fake-indexeddb/auto'],
    include: ['src/**/*.spec.ts'],
  },
})
