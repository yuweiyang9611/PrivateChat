<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useBreakpoints } from '@vueuse/core'
import { Search, Plus, Setting, VideoCamera } from '@element-plus/icons-vue'
import { useConversationsStore } from '@/stores/conversations'
import { useFamilyStore } from '@/stores/family'
import { useSessionStore } from '@/stores/session'
import { useUiStore } from '@/stores/ui'
import { useCallsStore } from '@/stores/calls'
import { callsSupported } from '@/features/calls/rtc/media'
import SetupPrompt from '@/components/SetupPrompt.vue'
import { frequentContacts } from './frequent'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const conversations = useConversationsStore()
const family = useFamilyStore()
const session = useSessionStore()
const ui = useUiStore()
const calls = useCallsStore()
const keyword = ref('')
// 桌面端左边那条图标栏里已经有"设置"；手机上长辈模式的底栏没有"我的"，要在这里留一个入口
const isDesktop = useBreakpoints({ desktop: 1024 }).greaterOrEqual('desktop')

/** 长辈模式：常联系的两个人，各一个大按钮，点一下就打视频过去 */
const QUICK_DIAL = 2
const quickDial = computed(() => (ui.elderMode ? frequentContacts(conversations.sorted, family.members, session.user?.id, QUICK_DIAL) : []))
const supported = callsSupported()
const dialDisabled = computed(() => !supported || !calls.enabled || calls.busy)

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
  <div class="flex h-full flex-col" :data-elder="ui.elderMode ? '' : undefined">
    <!-- 长辈模式：没有搜索和新建，只有标题；手机上右边留一个进设置的入口 -->
    <header v-if="ui.elderMode" class="flex shrink-0 items-center justify-between gap-3 px-4 pt-4 pb-2">
      <h1 class="text-2xl font-semibold">{{ t('elder.home') }}</h1>
      <router-link
        v-if="!isDesktop"
        to="/settings"
        class="touch-target flex items-center justify-center rounded-full"
        style="color: var(--pc-text-muted)"
        :title="t('nav.settings')"
        :aria-label="t('nav.settings')"
        data-testid="elder-settings"
      >
        <el-icon :size="28"><Setting /></el-icon>
      </router-link>
    </header>
    <div v-else class="flex items-center gap-2 p-3">
      <el-input v-model="keyword" :placeholder="t('chat.search')" :prefix-icon="Search" clearable maxlength="100" data-testid="conversation-search" @keydown.enter="searchEverything" />
      <el-button :icon="Plus" circle :title="t('chat.newGroup')" />
    </div>

    <SetupPrompt />

    <el-empty v-if="conversations.loaded && !conversations.sorted.length" :description="t('chat.noConversations')" />

    <ul class="min-h-0 flex-1 overflow-y-auto">
      <li v-if="keyword.trim() && !ui.elderMode">
        <button type="button" class="flex w-full items-center gap-3 px-3 py-2.5 text-left" data-testid="search-everything" @click="searchEverything()">
          <span class="flex h-11 w-11 shrink-0 items-center justify-center rounded-full" style="background: var(--pc-primary-soft); color: var(--pc-primary)">
            <el-icon :size="22"><Search /></el-icon>
          </span>
          <span class="min-w-0 flex-1 truncate text-sm">{{ t('search.everything', { q: keyword.trim() }) }}</span>
        </button>
      </li>
      <li v-for="c in conversations.sorted.filter((x) => ui.elderMode || !keyword || family.titleOf(x).includes(keyword))" :key="c.id">
        <router-link
          :to="`/messages/${c.id}`"
          class="flex items-center"
          :class="ui.elderMode ? 'gap-4 px-4 py-3.5' : 'gap-3 px-3 py-2.5'"
          :style="route.params.id === c.id ? 'background: var(--pc-primary-soft)' : ''"
          data-testid="conversation-row"
        >
          <el-badge :value="c.lastSeq - c.lastReadSeq" :hidden="c.lastSeq <= c.lastReadSeq" :max="99">
            <el-avatar :size="ui.elderMode ? 56 : 44" :style="`background: var(--pc-primary); font-size: ${ui.elderMode ? 24 : 16}px`">{{ (family.titleOf(c) || '?').slice(0, 1) }}</el-avatar>
          </el-badge>
          <div class="min-w-0 flex-1">
            <div class="flex items-baseline justify-between gap-2">
              <span class="truncate font-medium" :class="ui.elderMode ? 'text-lg' : ''">{{ family.titleOf(c) }}</span>
              <span class="shrink-0" :class="ui.elderMode ? 'text-sm' : 'text-xs'" style="color: var(--pc-text-muted)">{{ formatTime(c.lastMessageAt) }}</span>
            </div>
            <div class="truncate" :class="ui.elderMode ? 'text-base' : 'text-sm'" style="color: var(--pc-text-muted)">{{ c.lastMessagePreview ?? '' }}</div>
          </div>
        </router-link>
      </li>
    </ul>

    <!-- 长辈模式：常联系的人，固定在列表下面（拇指够得着），点一下就打视频 -->
    <section
      v-if="quickDial.length"
      class="flex shrink-0 flex-col gap-2 border-t px-4 py-3"
      style="border-color: var(--pc-border); background: var(--pc-surface)"
      :aria-label="t('elder.quickDial')"
      data-testid="elder-quick-dial"
    >
      <el-button
        v-for="m in quickDial"
        :key="m.id"
        type="primary"
        size="large"
        :icon="VideoCamera"
        :disabled="dialDisabled"
        class="pc-quick-dial"
        data-testid="elder-dial"
        :data-user-id="m.id"
        @click="calls.callMember(m.id, 'Video')"
      >
        <span class="truncate">{{ t('elder.callName', { name: m.displayName || m.username }) }}</span>
      </el-button>
    </section>
  </div>
</template>

<style scoped>
/* 一键拨打：整行宽的大按钮；Element 给相邻按钮加的左边距在竖排里不要 */
.pc-quick-dial.el-button {
  width: 100%;
  min-height: 64px;
  margin-left: 0;
  border-radius: 16px;
  font-size: 1.125rem;
  font-weight: 600;
}
.pc-quick-dial :deep(.el-icon) {
  font-size: 1.5rem;
}
</style>
