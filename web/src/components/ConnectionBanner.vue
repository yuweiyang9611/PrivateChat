<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { HubConnectionState } from '@microsoft/signalr'
import { connectionState } from '@/realtime/connection'
import { useSessionStore } from '@/stores/session'

const { t } = useI18n()
const session = useSessionStore()

const text = computed(() => {
  if (session.loading) return t('session.loading')
  if (connectionState.value === HubConnectionState.Reconnecting) return t('session.reconnecting')
  if (session.user && connectionState.value === HubConnectionState.Disconnected) return t('session.offline')
  return ''
})
</script>

<template>
  <div v-if="text" class="px-4 py-1 text-center text-xs" style="background: var(--pc-primary-soft); color: var(--pc-primary)">
    {{ text }}
  </div>
</template>
