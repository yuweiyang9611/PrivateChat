<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { watchDebounced } from '@vueuse/core'
import { HubConnectionState } from '@microsoft/signalr'
import { connectionState } from '@/realtime/connection'
import { online } from '@/net/online'
import { useSessionStore } from '@/stores/session'
import { useMessagesStore } from '@/stores/messages'

const { t } = useI18n()
const session = useSessionStore()
const messages = useMessagesStore()

/** 断网立即提示；"连接中 / 连不上服务器"要持续 1.5 秒以上才显示，避免启动和短暂抖动时闪一下 */
const offlineText = computed(() => {
  if (!session.user || online.value) return ''
  return messages.outboxCount > 0 ? t('session.offlineQueued', { n: messages.outboxCount }) : t('session.offlineBanner')
})
const serverText = computed(() => {
  if (!session.user || !online.value) return ''
  const s = connectionState.value
  if (s === HubConnectionState.Reconnecting || s === HubConnectionState.Connecting) return t('session.reconnecting')
  if (s === HubConnectionState.Disconnected) return t('session.unreachable')
  return ''
})
// 起始为空、从挂载起算延迟（refDebounced 的初值就是当前值，启动那一下会直接显示"连不上"）
const serverTextDelayed = ref('')
watchDebounced(serverText, (v) => { serverTextDelayed.value = v }, { debounce: 1500, immediate: true })

const text = computed(() => offlineText.value || (serverText.value && serverTextDelayed.value ? serverText.value : ''))
</script>

<template>
  <div
    v-if="text"
    class="px-4 py-1 text-center text-xs"
    style="background: var(--pc-primary-soft); color: var(--pc-primary)"
    role="status"
    data-testid="connection-banner"
  >
    {{ text }}
  </div>
</template>
