<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Document, VideoPlay } from '@element-plus/icons-vue'
import VoiceBubble from './VoiceBubble.vue'
import { parseMeta, type LocalMessage } from '@/db'
import { blobUrl, formatSize } from '@/upload'
import { useMessagesStore } from '@/stores/messages'

const props = defineProps<{ message: LocalMessage }>()
const { t } = useI18n()
const messages = useMessagesStore()

const att = computed(() => props.message.attachments[0])
const meta = computed(() => parseMeta(att.value?.meta))
const pending = computed(() => props.message.status !== 'sent' || !att.value?.blobId)
const version = computed(() => (att.value?.blobId ? messages.blobVersion[att.value.blobId] ?? 0 : 0))

/** 气泡内图片按原图比例占位，最宽 260、最高 320 */
const box = computed(() => {
  const w = meta.value.width ?? 4
  const h = meta.value.height ?? 3
  const scale = Math.min(260 / w, 320 / h, 1)
  return { width: Math.max(80, Math.round(w * scale)), height: Math.max(60, Math.round(h * scale)) }
})

const thumbUrl = computed(() =>
  pending.value ? props.message.localUrl ?? '' : blobUrl(att.value!.blobId, { v: 'thumb-320', r: version.value }),
)
const fullUrl = computed(() => (pending.value ? '' : blobUrl(att.value!.blobId, { v: 'thumb-1280', r: version.value })))
const originalUrl = computed(() => (pending.value ? props.message.localUrl ?? '' : blobUrl(att.value!.blobId, { name: meta.value.name })))
/** 语音统一请求转码后的 m4a；没生成时服务端回退原件（录制端浏览器本来就能放） */
const audioUrl = computed(() => (pending.value ? props.message.localUrl ?? '' : blobUrl(att.value!.blobId, { v: 'audio-m4a', r: version.value })))
const downloadUrl = computed(() => (pending.value ? '' : blobUrl(att.value!.blobId, { name: meta.value.name, download: true })))
const percent = computed(() => Math.round((props.message.uploadProgress ?? 0) * 100))
</script>

<template>
  <div v-if="att" class="relative">
    <!-- 图片 -->
    <div v-if="att.kind === 'Image'" class="overflow-hidden rounded-lg" :style="{ width: box.width + 'px', height: box.height + 'px', background: 'var(--pc-border)' }">
      <img v-if="pending" :src="thumbUrl" class="h-full w-full object-cover" :alt="meta.name" />
      <el-image
        v-else
        :src="thumbUrl"
        :preview-src-list="[fullUrl]"
        :initial-index="0"
        preview-teleported
        hide-on-click-modal
        fit="cover"
        class="h-full w-full"
        :alt="meta.name"
      />
    </div>

    <!-- 视频：M1 先直接播原件，转码 720p 等 ffmpeg 接入 -->
    <div v-else-if="att.kind === 'Video'" class="overflow-hidden rounded-lg" style="width: 260px; background: #000">
      <video v-if="pending" :src="originalUrl" muted playsinline class="block max-h-[320px] w-full" />
      <video v-else :src="originalUrl" controls preload="metadata" playsinline class="block max-h-[320px] w-full" />
    </div>

    <!-- 语音 / 音频 -->
    <div v-else-if="att.kind === 'Audio'" class="px-1 py-1">
      <VoiceBubble :src="audioUrl" :duration="meta.duration" :pending="pending" />
    </div>

    <!-- 文件卡片 -->
    <a
      v-else
      :href="downloadUrl || undefined"
      class="flex items-center gap-3 rounded-lg px-1 py-1"
      style="min-width: 200px; max-width: 260px; color: inherit; text-decoration: none"
      :download="meta.name"
    >
      <el-icon :size="34" style="opacity: 0.85"><Document /></el-icon>
      <div class="min-w-0 flex-1">
        <div class="truncate text-sm font-medium" :title="meta.name">{{ meta.name }}</div>
        <div class="text-xs opacity-70">{{ meta.size != null ? formatSize(meta.size) : '' }}<span v-if="!pending"> · {{ t('chat.download') }}</span></div>
      </div>
    </a>

    <!-- 上传进度遮罩 -->
    <div
      v-if="pending && message.status === 'pending'"
      class="absolute inset-0 flex flex-col items-center justify-center gap-1 rounded-lg"
      style="background: rgba(0, 0, 0, 0.35); color: #fff"
    >
      <el-progress type="circle" :percentage="percent" :width="44" :stroke-width="4" color="#fff" :show-text="false" />
      <span class="text-xs">{{ t('chat.uploading', { p: percent }) }}</span>
      <button class="text-xs underline" @click.prevent="messages.cancel(message.conversationId, message.clientMessageId)">{{ t('chat.cancelUpload') }}</button>
    </div>
    <el-icon v-if="att.kind === 'Video' && pending && message.status !== 'pending'" :size="36" class="absolute inset-0 m-auto" style="color: #fff"><VideoPlay /></el-icon>
  </div>
</template>
