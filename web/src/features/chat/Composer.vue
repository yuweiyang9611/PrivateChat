<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef } from 'vue'
import { useI18n } from 'vue-i18n'
import { useThrottleFn } from '@vueuse/core'
import { ElMessage } from 'element-plus'
import { Paperclip, Microphone, Close } from '@element-plus/icons-vue'
import { useMessagesStore } from '@/stores/messages'
import { notifyTyping } from '@/realtime/connection'
import { formatSize } from '@/upload'
import { RecorderUnavailableError, VoiceRecorder } from '@/upload/recorder'

const MAX_FILES = 9
const MAX_SIZE = 8 * 1024 * 1024 * 1024 // 与服务端 tus 上限一致
const MAX_SECONDS = 300

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

// ---- 文件 ----
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

// ---- 语音：点一下开始，再点一下发送；长辈不用按住不放 ----
// shallowRef：不要把 MediaRecorder / MediaStream 包进响应式代理
const recorder = shallowRef<VoiceRecorder | null>(null)
const recording = computed(() => recorder.value !== null)
const seconds = computed(() => recorder.value?.seconds.value ?? 0)
const level = computed(() => recorder.value?.level.value ?? 0)
const bars = 14

function fmt(s: number): string {
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

async function toggleRecord(): Promise<void> {
  if (recording.value) {
    await finishRecord()
    return
  }
  const r = new VoiceRecorder(MAX_SECONDS, () => void finishRecord())
  try {
    await r.start()
    recorder.value = r
  } catch (e) {
    const reason = e instanceof RecorderUnavailableError ? e.reason : 'unsupported'
    ElMessage.warning(t(reason === 'insecure' ? 'chat.micInsecure' : reason === 'denied' ? 'chat.micDenied' : 'chat.micUnsupported'))
  }
}

async function finishRecord(): Promise<void> {
  const r = recorder.value
  if (!r) return
  recorder.value = null
  const result = await r.stop()
  if (!result) {
    ElMessage.info(t('chat.recordTooShort'))
    return
  }
  await messages.sendVoice(props.conversationId, result.file, result.duration)
}

function cancelRecord(): void {
  recorder.value?.cancel()
  recorder.value = null
}

onBeforeUnmount(cancelRecord)
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

    <!-- 录音态：整条输入栏换成计时 + 波形 + 取消 / 发送 -->
    <template v-if="recording">
      <el-button :icon="Close" circle text :title="t('common.cancel')" data-testid="record-cancel" @click="cancelRecord" />
      <div class="flex h-10 flex-1 items-center gap-3 rounded-full px-4" style="background: var(--pc-primary-soft); color: var(--pc-primary)">
        <span class="h-2.5 w-2.5 animate-pulse rounded-full" style="background: #e5484d" />
        <span class="text-sm tabular-nums">{{ fmt(seconds) }}</span>
        <div class="flex h-6 flex-1 items-center gap-[3px]" aria-hidden="true">
          <span
            v-for="i in bars"
            :key="i"
            class="w-[3px] rounded-full"
            style="background: currentColor; transition: height 0.1s"
            :style="{ height: Math.max(3, Math.round(24 * level * (0.4 + 0.6 * Math.abs(Math.sin((i * 7 + seconds * 3) * 0.9))))) + 'px' }"
          />
        </div>
        <span class="text-xs">{{ t('chat.recording') }}</span>
      </div>
      <el-button type="primary" data-testid="record-send" @click="finishRecord">{{ t('chat.recordStop') }}</el-button>
    </template>

    <template v-else>
      <el-button :icon="Paperclip" circle text :title="t('chat.attach')" @click="pickFiles" />
      <el-button :icon="Microphone" circle text :title="t('chat.recordTap')" data-testid="record-start" @click="toggleRecord" />
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
    </template>
  </footer>
</template>
