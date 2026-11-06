<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ChatDotRound, Phone, Folder, PictureRounded, User } from '@element-plus/icons-vue'
import ConnectionBanner from '@/components/ConnectionBanner.vue'
import { useConversationsStore } from '@/stores/conversations'
import { useUiStore } from '@/stores/ui'

const { t } = useI18n()
const route = useRoute()
const conversations = useConversationsStore()
const ui = useUiStore()

// 聊天详情页全屏，不显示底部 Tab
const showTabs = computed(() => !route.path.startsWith('/messages/'))

const tabs = computed(() => {
  const all = [
    { to: '/messages', section: 'messages', icon: ChatDotRound, label: t('nav.messages'), badge: conversations.unreadTotal },
    { to: '/calls', section: 'calls', icon: Phone, label: t('nav.calls') },
    { to: '/drive', section: 'drive', icon: Folder, label: t('nav.drive') },
    { to: '/moments', section: 'moments', icon: PictureRounded, label: t('nav.moments') },
    { to: '/settings', section: 'settings', icon: User, label: t('nav.me') },
  ]
  // 长辈模式：只留 消息 / 通话 / 时光
  return ui.elderMode ? all.filter((x) => ['messages', 'calls', 'moments'].includes(x.section)) : all
})

const activeSection = computed(() => (route.meta.section === 'devices' || route.meta.section === 'family' ? 'settings' : route.meta.section))
</script>

<template>
  <div class="flex h-full flex-col">
    <ConnectionBanner />
    <main class="min-h-0 flex-1 overflow-y-auto">
      <router-view />
    </main>
    <nav
      v-if="showTabs"
      class="flex shrink-0 border-t"
      style="border-color: var(--pc-border); background: var(--pc-surface); padding-bottom: env(safe-area-inset-bottom)"
    >
      <router-link
        v-for="tab in tabs"
        :key="tab.to"
        :to="tab.to"
        class="flex flex-1 flex-col items-center justify-center py-2 text-xs"
        :style="`min-height: ${ui.elderMode ? 64 : 56}px; color: ${activeSection === tab.section ? 'var(--pc-primary)' : 'var(--pc-text-muted)'}`"
      >
        <el-badge :value="tab.badge" :hidden="!tab.badge" :max="99">
          <el-icon :size="ui.elderMode ? 28 : 24"><component :is="tab.icon" /></el-icon>
        </el-badge>
        <span class="mt-0.5">{{ tab.label }}</span>
      </router-link>
    </nav>
  </div>
</template>
