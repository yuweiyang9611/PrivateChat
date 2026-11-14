<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ChatDotRound, Phone, Folder, PictureRounded, Monitor, Setting, User, Search } from '@element-plus/icons-vue'
import ConversationList from '@/features/chat/ConversationList.vue'
import ConnectionBanner from '@/components/ConnectionBanner.vue'
import { useConversationsStore } from '@/stores/conversations'
import { useSessionStore } from '@/stores/session'

const { t } = useI18n()
const route = useRoute()
const conversations = useConversationsStore()
const session = useSessionStore()

const nav = computed(() => [
  { to: '/messages', section: 'messages', icon: ChatDotRound, label: t('nav.messages'), badge: conversations.unreadTotal },
  { to: '/calls', section: 'calls', icon: Phone, label: t('nav.calls') },
  { to: '/drive', section: 'drive', icon: Folder, label: t('nav.drive') },
  { to: '/moments', section: 'moments', icon: PictureRounded, label: t('nav.moments') },
  { to: '/devices', section: 'devices', icon: Monitor, label: t('nav.devices') },
  { to: '/search', section: 'search', icon: Search, label: t('nav.search') },
])
</script>

<template>
  <div class="flex h-full">
    <!-- 左侧 72px 图标栏 -->
    <nav class="flex w-[72px] shrink-0 flex-col items-center gap-1 border-r py-3" style="border-color: var(--pc-border); background: var(--pc-surface)">
      <router-link to="/settings" class="mb-3">
        <el-avatar :size="40" style="background: var(--pc-primary)">{{ session.user?.displayName?.slice(0, 1) ?? '家' }}</el-avatar>
      </router-link>
      <router-link
        v-for="item in nav"
        :key="item.to"
        :to="item.to"
        class="touch-target flex flex-col items-center justify-center rounded-xl px-1 py-2 text-xs"
        :class="route.meta.section === item.section ? 'font-semibold' : ''"
        :style="route.meta.section === item.section ? 'background: var(--pc-primary-soft); color: var(--pc-primary)' : 'color: var(--pc-text-muted)'"
      >
        <el-badge :value="item.badge" :hidden="!item.badge" :max="99">
          <el-icon :size="22"><component :is="item.icon" /></el-icon>
        </el-badge>
        <span class="mt-1">{{ item.label }}</span>
      </router-link>
      <div class="flex-1" />
      <router-link to="/family" class="touch-target flex items-center justify-center" style="color: var(--pc-text-muted)" :title="t('nav.family')">
        <el-icon :size="22"><User /></el-icon>
      </router-link>
      <router-link to="/settings" class="touch-target flex items-center justify-center" style="color: var(--pc-text-muted)" :title="t('nav.settings')">
        <el-icon :size="22"><Setting /></el-icon>
      </router-link>
    </nav>

    <!-- 中栏 320px 列表（仅消息分区） -->
    <aside v-if="route.meta.panel === 'conversations'" class="w-[320px] shrink-0 overflow-y-auto border-r" style="border-color: var(--pc-border); background: var(--pc-surface)">
      <ConversationList />
    </aside>

    <!-- 右栏内容 -->
    <main class="flex min-w-0 flex-1 flex-col">
      <ConnectionBanner />
      <router-view class="min-h-0 flex-1" />
    </main>
  </div>
</template>
