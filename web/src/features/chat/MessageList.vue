<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import MessageItem from './MessageItem.vue'
import { useMessagesStore } from '@/stores/messages'
import { useSessionStore } from '@/stores/session'

const props = defineProps<{ conversationId: string }>()
const messages = useMessagesStore()
const session = useSessionStore()
const scroller = ref<HTMLElement | null>(null)

// M0 直接渲染全部；后续换成以 seq 为 key 的虚拟滚动
const list = computed(() => messages.list(props.conversationId))

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
    <MessageItem v-for="m in list" :key="m.clientMessageId || m.id" :message="m" :mine="m.senderId === session.user?.id" />
  </div>
</template>
