<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useThrottleFn } from '@vueuse/core'
import { ElMessage } from 'element-plus'
import { Paperclip, Microphone } from '@element-plus/icons-vue'
import { useMessagesStore } from '@/stores/messages'
import { notifyTyping } from '@/realtime/connection'
import { formatSize } from '@/upload'

const MAX_FILES = 9
const MAX_SIZE = 8 * 1024 * 1024 * 1024 // 与服务端 tus 上限一致

const props = defineProps<{ conversationId: string }>()
const { t } = useI18n()
const messages = useMessagesStore()
const text = ref('')
const fileInput = ref<HTMLInputElement | null>(null)
const dragging = ref(false)

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

function pickFiles(): void {
  fileInput.value?.click()
}

function onFilesChosen(evt: Event): void {
  const input = evt.target as HTMLInputElement
  const files = Array.from(input.files ?? [])
  input.value = ''
  void sendFiles(files)
}

/** 粘贴截图 / 文件 */
function onPaste(evt: ClipboardEvent): void {
  const files = Array.from(evt.clipboardData?.files ?? [])
  if (!files.length) return
  evt.preventDefault()
  void sendFiles(files)
}

function onDrop(evt: DragEvent): void {
  dragging.value = false
  const files = Array.from(evt.dataTransfer?.files ?? [])
  if (files.length) void sendFiles(files)
}

async function sendFiles(files: File[]): Promise<void> {
  if (!files.length) return
  if (files.length > MAX_FILES) {
    ElMessage.warning(t('chat.tooMany', { n: MAX_FILES }))
    files = files.slice(0, MAX_FILES)
  }
  const ok = files.filter((f) => f.size <= MAX_SIZE && f.size > 0)
  if (ok.length < files.length) ElMessage.warning(t('chat.tooLarge', { max: formatSize(MAX_SIZE) }))
  if (ok.length) await messages.sendFiles(props.conversationId, ok)
}
</script>

<template>
  <footer
    class="relative flex items-end gap-2 border-t p-2"
    style="border-color: var(--pc-border); background: var(--pc-surface); padding-bottom: calc(0.5rem + env(safe-area-inset-bottom))"
    @dragover.prevent="dragging = true"
    @dragleave.prevent="dragging = false"
    @drop.prevent="onDrop"
  >
    <div
      v-if="dragging"
      class="pointer-events-none absolute inset-0 z-10 flex items-center justify-center rounded text-sm"
      style="background: var(--pc-primary-soft); color: var(--pc-primary); border: 2px dashed var(--pc-primary)"
    >
      {{ t('chat.dropHint') }}
    </div>
    <input ref="fileInput" type="file" multiple class="hidden" data-testid="file-input" @change="onFilesChosen" />
    <el-button :icon="Paperclip" circle text :title="t('chat.attach')" @click="pickFiles" />
    <el-button :icon="Microphone" circle text disabled title="M1" />
    <el-input
      v-model="text"
      type="textarea"
      :autosize="{ minRows: 1, maxRows: 5 }"
      :placeholder="t('chat.placeholder')"
      resize="none"
      @input="typing"
      @keydown="onKeydown"
      @paste="onPaste"
    />
    <el-button type="primary" :disabled="!text.trim()" @click="send">{{ t('chat.send') }}</el-button>
  </footer>
</template>
