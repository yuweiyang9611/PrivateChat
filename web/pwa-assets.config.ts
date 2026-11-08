import { defineConfig, minimal2023Preset } from '@vite-pwa/assets-generator/config'

// 由 public/icon.svg 生成安装图标：npm run gen:icons（产物提交进仓库，构建时不再生成）
// 底色用主色暖橙，圆角处透明像素被填满，Android 自适应图标与 iOS 主屏图标都是满版方块
const brand = '#e8873a'

export default defineConfig({
  headLinkOptions: { preset: '2023' },
  preset: {
    ...minimal2023Preset,
    maskable: {
      ...minimal2023Preset.maskable,
      padding: 0.3,
      resizeOptions: { background: brand, fit: 'contain' },
    },
    apple: {
      ...minimal2023Preset.apple,
      padding: 0.15,
      resizeOptions: { background: brand, fit: 'contain' },
    },
  },
  images: ['public/icon.svg'],
})
