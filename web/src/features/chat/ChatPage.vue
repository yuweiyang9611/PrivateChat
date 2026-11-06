<script setup lang="ts">
import { computed, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowLeft, Phone, VideoCamera, MoreFilled } from '@element-plus/icons-vue'
import MessageList from './MessageList.vue'
import Composer from './Composer.vue'
import { useConversationsStore } from '@/stores/conversations'
import { useMessagesStore } from '@/stores/messages'
import { typing } from '@/realtime/connection'

const props = defineProps<{ id: string }>()
const { t } = useI18n()
const conversations = useConversationsStore()
const messages = useMessagesStore()

const conversation = computed(() => conversations.byId(props.id))
const typingUsers = computed(() => typing.value[props.id] ?? [])

watch(
  () => props.id,
  async (id) => {
    await messages.load(id)
    await messages.read(id)
  },
  { immediate: true },
)
</script>

<template>
  <div class="flex h-full flex-col">
    <header class="flex items-center gap-2 border-b px-3 py-2" style="border-color: var(--pc-border); background: var(--pc-surface)">
      <router-link to="/messages" class="touch-target flex items-center justify-center lg:hidden">
        <el-icon :size="22"><ArrowLeft /></el-icon>
      </router-link>
      <div class="min-w-0 flex-1">
        <div class="truncate font-semibold">{{ conversation?.title ?? '…' }}</div>
        <div class="text-xs" style="color: var(--pc-text-muted)">
          <span v-if="typingUsers.length">正在输入…</span>
          <span v-else-if="conversation">{{ t('chat.members', { n: conversation.memberIds.length }) }}</span>
        </div>
      </div>
      <el-button :icon="Phone" circle text disabled title="M3" />
      <el-button :icon="VideoCamera" circle text disabled title="M3" />
      <el-button :icon="MoreFilled" circle text />
    </header>

    <MessageList :conversation-id="id" class="min-h-0 flex-1" />

    <Composer :conversation-id="id" />
  </div>
</template>
