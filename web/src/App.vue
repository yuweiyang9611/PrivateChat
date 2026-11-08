<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { useBreakpoints } from '@vueuse/core'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import DesktopLayout from '@/layouts/DesktopLayout.vue'
import MobileLayout from '@/layouts/MobileLayout.vue'
import LocalLoginPage from '@/features/auth/LocalLoginPage.vue'
import UpdatePrompt from '@/components/UpdatePrompt.vue'
import { useSessionStore } from '@/stores/session'
import { useConversationsStore } from '@/stores/conversations'
import { useMessagesStore } from '@/stores/messages'
import { useUiStore } from '@/stores/ui'
import { refreshPushState } from '@/push'

// 设计稿 5.4：≥1024px 桌面三栏，否则手机底部 Tab
const breakpoints = useBreakpoints({ desktop: 1024 })
const isDesktop = breakpoints.greaterOrEqual('desktop')
const layout = computed(() => (isDesktop.value ? DesktopLayout : MobileLayout))

const session = useSessionStore()
const conversations = useConversationsStore()
const messages = useMessagesStore()
useUiStore()

// 有了用户（缓存身份冷启动、服务器确认、密码登录）就加载会话列表并恢复发件箱；都先读本地，离线也能用
watch(
  () => session.user?.id,
  async (id, prev) => {
    if (!id || id === prev) return
    await conversations.load()
    await messages.restoreOutbox()
    void refreshPushState()
  },
)

onMounted(() => {
  void session.load()
})
</script>

<template>
  <el-config-provider :locale="zhCn">
    <div v-if="session.loading" class="flex min-h-dvh items-center justify-center opacity-60">{{ $t('session.loading') }}</div>
    <LocalLoginPage v-else-if="!session.user" />
    <component :is="layout" v-else />
    <UpdatePrompt />
  </el-config-provider>
</template>
