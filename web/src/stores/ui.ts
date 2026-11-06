import { defineStore } from 'pinia'
import { useDark, useStorage } from '@vueuse/core'
import { watch } from 'vue'

/** 界面偏好：暗色、长辈模式；均存浏览器本地，和账号无关 */
export const useUiStore = defineStore('ui', () => {
  const isDark = useDark({ selector: 'html', attribute: 'class', valueDark: 'dark', valueLight: '' })
  const elderMode = useStorage<boolean>('pc.elderMode', false)

  watch(
    elderMode,
    (on) => document.documentElement.classList.toggle('elder', on),
    { immediate: true },
  )

  return { isDark, elderMode }
})
