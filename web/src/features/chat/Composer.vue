<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useThrottleFn } from '@vueuse/core'
import { Paperclip, Microphone } from '@element-plus/icons-vue'
import { useMessagesStore } from '@/stores/messages'
import { notifyTyping } from '@/realtime/connection'

const props = defineProps<{ conversationId: string }>()
const { t } = useI18n()
const messages = useMessagesStore()
const text = ref('')

const typing = useThrottleFn(() => void notifyTyping(props.conversationId), 3_000)

async function send(): Promise<void> {
  const body = text.value.trim()
  if (!body) return
  text.value = ''
  await messages.sendText(props.conversationId, body)
}

function onKeydown(evt: Event | KeyboardEvent): void {
  const e = evt as KeyboardEvent
  // Enter 发送，Shift+Enter 换行
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
    e.preventDefault()
    void send()
  }
}
</script>

<template>
  <footer class="flex items-end gap-2 border-t p-2" style="border-color: var(--pc-border); background: var(--pc-surface); padding-bottom: calc(0.5rem + env(safe-area-inset-bottom))">
    <el-button :icon="Paperclip" circle text disabled title="M2" />
    <el-button :icon="Microphone" circle text disabled title="M1" />
    <el-input
      v-model="text"
      type="textarea"
      :autosize="{ minRows: 1, maxRows: 5 }"
      :placeholder="t('chat.placeholder')"
      resize="none"
      @input="typing"
      @keydown="onKeydown"
    />
    <el-button type="primary" :disabled="!text.trim()" @click="send">{{ t('chat.send') }}</el-button>
  </footer>
</template>
