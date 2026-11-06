<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useBreakpoints } from '@vueuse/core'
import ConversationList from './ConversationList.vue'

const { t } = useI18n()
// 桌面端列表已在中栏，这里只放空状态；手机端这里就是会话列表
const isDesktop = useBreakpoints({ desktop: 1024 }).greaterOrEqual('desktop')
</script>

<template>
  <div v-if="isDesktop" class="flex h-full items-center justify-center" style="color: var(--pc-text-muted)">
    {{ t('chat.empty') }}
  </div>
  <ConversationList v-else />
</template>
