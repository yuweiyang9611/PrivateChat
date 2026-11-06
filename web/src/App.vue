<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useBreakpoints } from '@vueuse/core'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import DesktopLayout from '@/layouts/DesktopLayout.vue'
import MobileLayout from '@/layouts/MobileLayout.vue'
import LocalLoginPage from '@/features/auth/LocalLoginPage.vue'
import { useSessionStore } from '@/stores/session'
import { useConversationsStore } from '@/stores/conversations'
import { useUiStore } from '@/stores/ui'
import { refreshPushState } from '@/push'

// 设计稿 5.4：≥1024px 桌面三栏，否则手机底部 Tab
const breakpoints = useBreakpoints({ desktop: 1024 })
const isDesktop = breakpoints.greaterOrEqual('desktop')
const layout = computed(() => (isDesktop.value ? DesktopLayout : MobileLayout))

const session = useSessionStore()
const conversations = useConversationsStore()
useUiStore()

onMounted(async () => {
  await session.load()
  if (session.user) {
    await conversations.load()
    void refreshPushState()
  }
})
</script>

<template>
  <el-config-provider :locale="zhCn">
    <div v-if="session.loading" class="flex min-h-dvh items-center justify-center opacity-60">{{ $t('session.loading') }}</div>
    <LocalLoginPage v-else-if="!session.user" />
    <component :is="layout" v-else />
  </el-config-provider>
</template>
