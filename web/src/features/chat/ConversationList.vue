<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Search, Plus } from '@element-plus/icons-vue'
import { useConversationsStore } from '@/stores/conversations'
import { useFamilyStore } from '@/stores/family'
import SetupPrompt from '@/components/SetupPrompt.vue'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const conversations = useConversationsStore()
const family = useFamilyStore()
const keyword = ref('')

/** 回车或点"搜索更多"：到搜索页找消息、文件、动态（这个框本身只按会话名过滤） */
function searchEverything(e?: Event): void {
  const k = e as KeyboardEvent | undefined
  if (k && (k.isComposing || k.keyCode === 229)) return // 输入法上屏的回车
  const q = keyword.value.trim()
  if (q) void router.push({ path: '/search', query: { q } })
}

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
      <el-input v-model="keyword" :placeholder="t('chat.search')" :prefix-icon="Search" clearable maxlength="100" data-testid="conversation-search" @keydown.enter="searchEverything" />
      <el-button :icon="Plus" circle :title="t('chat.newGroup')" />
    </div>

    <SetupPrompt />

    <el-empty v-if="conversations.loaded && !conversations.sorted.length" :description="t('chat.noConversations')" />

    <ul class="flex-1 overflow-y-auto">
      <li v-if="keyword.trim()">
        <button type="button" class="flex w-full items-center gap-3 px-3 py-2.5 text-left" data-testid="search-everything" @click="searchEverything()">
          <span class="flex h-11 w-11 shrink-0 items-center justify-center rounded-full" style="background: var(--pc-primary-soft); color: var(--pc-primary)">
            <el-icon :size="22"><Search /></el-icon>
          </span>
          <span class="min-w-0 flex-1 truncate text-sm">{{ t('search.everything', { q: keyword.trim() }) }}</span>
        </button>
      </li>
      <li v-for="c in conversations.sorted.filter((x) => !keyword || family.titleOf(x).includes(keyword))" :key="c.id">
        <router-link
          :to="`/messages/${c.id}`"
          class="flex items-center gap-3 px-3 py-2.5"
          :style="route.params.id === c.id ? 'background: var(--pc-primary-soft)' : ''"
        >
          <el-badge :value="c.lastSeq - c.lastReadSeq" :hidden="c.lastSeq <= c.lastReadSeq" :max="99">
            <el-avatar :size="44" style="background: var(--pc-primary)">{{ (family.titleOf(c) || '?').slice(0, 1) }}</el-avatar>
          </el-badge>
          <div class="min-w-0 flex-1">
            <div class="flex items-baseline justify-between gap-2">
              <span class="truncate font-medium">{{ family.titleOf(c) }}</span>
              <span class="shrink-0 text-xs" style="color: var(--pc-text-muted)">{{ formatTime(c.lastMessageAt) }}</span>
            </div>
            <div class="truncate text-sm" style="color: var(--pc-text-muted)">{{ c.lastMessagePreview ?? '' }}</div>
          </div>
        </router-link>
      </li>
    </ul>
  </div>
</template>
