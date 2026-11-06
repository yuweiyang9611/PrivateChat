<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import AttachmentView from './AttachmentView.vue'
import { useMessagesStore } from '@/stores/messages'
import { useSessionStore } from '@/stores/session'

const props = defineProps<{ conversationId: string }>()
const { t } = useI18n()
const messages = useMessagesStore()
const session = useSessionStore()
const scroller = ref<HTMLElement | null>(null)

// M0 直接渲染全部；M1 换成以 seq 为 key 的虚拟滚动
const list = computed(() => messages.list(props.conversationId))

function isMine(senderId: string): boolean {
  return senderId === session.user?.id
}

function time(iso: string): string {
  return new Date(iso).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
}

watch(
  () => list.value.length,
  async () => {
    await nextTick()
    scroller.value?.scrollTo({ top: scroller.value.scrollHeight })
  },
  { immediate: true },
)
</script>

<template>
  <div ref="scroller" class="overflow-y-auto px-4 py-3">
    <div v-for="m in list" :key="m.clientMessageId || m.id" class="mb-2 flex" :class="isMine(m.senderId) ? 'justify-end' : 'justify-start'">
      <div
        v-if="m.type === 'System' || m.type === 'Call'"
        class="mx-auto my-1 rounded-full px-3 py-1 text-xs"
        style="background: var(--pc-border); color: var(--pc-text-muted)"
      >
        {{ m.body }}
      </div>
      <div
        v-else
        class="max-w-[75%] rounded-2xl text-[15px] leading-relaxed"
        :class="m.attachments.length && !m.deleted ? 'p-1' : 'px-3 py-2'"
        :style="isMine(m.senderId)
          ? 'background: var(--pc-primary); color: #fff; border-bottom-right-radius: 4px'
          : 'background: var(--pc-surface); border: 1px solid var(--pc-border); border-bottom-left-radius: 4px'"
      >
        <div v-if="m.deleted" class="italic opacity-70">{{ t('chat.recalled') }}</div>
        <AttachmentView v-else-if="m.attachments.length" :message="m" />
        <div v-else class="whitespace-pre-wrap break-words">{{ m.body }}</div>
        <div class="mt-1 text-right text-[11px] opacity-70" :class="m.attachments.length ? 'px-2 pb-1' : ''">
          <span v-if="m.status === 'pending'">{{ t('chat.pending') }}</span>
          <template v-else-if="m.status === 'failed'">
            <button class="underline" @click="messages.retry(conversationId, m.clientMessageId)">{{ t('chat.failed') }}</button>
            <button v-if="m.attachments.length" class="ml-2 underline" @click="messages.cancel(conversationId, m.clientMessageId)">{{ t('chat.cancelUpload') }}</button>
          </template>
          <span v-else>{{ time(m.createdAt) }}</span>
        </div>
      </div>
    </div>
  </div>
</template>
