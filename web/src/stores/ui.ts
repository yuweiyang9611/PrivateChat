import { defineStore } from 'pinia'
import { useDark, useStorage } from '@vueuse/core'
import { computed, watch } from 'vue'
import { choiceFor, effectiveElder, ELDER_KEY, migrateLegacy, parseChoice, type ElderChoice } from './elder'
import { useSessionStore } from './session'

/**
 * 界面偏好：暗色、长辈模式。
 * 暗色只存浏览器本地；长辈模式是"账号上的开关 + 这台设备上的选择"（见 elder.ts）。
 */
export const useUiStore = defineStore('ui', () => {
  const isDark = useDark({ selector: 'html', attribute: 'class', valueDark: 'dark', valueLight: '' })

  try {
    migrateLegacy(window.localStorage)
  } catch {
    // 隐私模式等读不了本地存储：就按没拨过算
  }
  /** 这台设备上自己拨过的选择；null 是没拨过，跟账号 */
  const elderChoice = useStorage<ElderChoice>(ELDER_KEY, null, undefined, {
    serializer: { read: parseChoice, write: (v) => v ?? '' },
    writeDefaults: false,
  })
  const session = useSessionStore()
  /** 管理员在"家庭"页给这个账号开的 */
  const elderFromAccount = computed(() => session.user?.elderMode ?? false)

  const elderMode = computed<boolean>({
    get: () => effectiveElder(elderChoice.value, elderFromAccount.value),
    set: (on) => {
      elderChoice.value = choiceFor(on, elderFromAccount.value)
    },
  })

  watch(elderMode, (on) => document.documentElement.classList.toggle('elder', on), { immediate: true })

  return { isDark, elderMode, elderChoice, elderFromAccount }
})
