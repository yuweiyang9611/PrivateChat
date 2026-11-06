<script setup lang="ts">
import { ref } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Search, Plus } from '@element-plus/icons-vue'
import { useConversationsStore } from '@/stores/conversations'
import PushPrompt from '@/components/PushPrompt.vue'

const { t } = useI18n()
const route = useRoute()
const conversations = useConversationsStore()
const keyword = ref('')

function formatTime(iso?: string | null): string {
  if (!iso) return ''
  const d = new Date(iso)
  const now = new Date()
  const sameDay = d.toDateString() === now.toDateString()
  return sameDay
    ? d.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
    : d.toLocaleDateString('zh-CN', { month: 'numeric', day: 'numeric' })
}
</script>

<template>
  <div class="flex h-full flex-col">
    <div class="flex items-center gap-2 p-3">
      <el-input v-model="keyword" :placeholder="t('chat.search')" :prefix-icon="Search" clearable />
      <el-button :icon="Plus" circle :title="t('chat.newGroup')" />
    </div>

    <PushPrompt />

    <el-empty v-if="conversations.loaded && !conversations.sorted.length" :description="t('chat.noConversations')" />

    <ul class="flex-1 overflow-y-auto">
      <li v-for="c in conversations.sorted.filter((x) => !keyword || (x.title ?? '').includes(keyword))" :key="c.id">
        <router-link
          :to="`/messages/${c.id}`"
          class="flex items-center gap-3 px-3 py-2.5"
          :style="route.params.id === c.id ? 'background: var(--pc-primary-soft)' : ''"
        >
          <el-badge :value="c.lastSeq - c.lastReadSeq" :hidden="c.lastSeq <= c.lastReadSeq" :max="99">
            <el-avatar :size="44" style="background: var(--pc-primary)">{{ (c.title ?? '?').slice(0, 1) }}</el-avatar>
          </el-badge>
          <div class="min-w-0 flex-1">
            <div class="flex items-baseline justify-between gap-2">
              <span class="truncate font-medium">{{ c.title }}</span>
              <span class="shrink-0 text-xs" style="color: var(--pc-text-muted)">{{ formatTime(c.lastMessageAt) }}</span>
            </div>
            <div class="truncate text-sm" style="color: var(--pc-text-muted)">{{ c.lastMessagePreview ?? '' }}</div>
          </div>
        </router-link>
      </li>
    </ul>
  </div>
</template>
