<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { Close, RefreshRight, VideoPause, VideoPlay } from '@element-plus/icons-vue'
import { useDriveUploadsStore, type UploadItem } from '@/stores/driveUploads'
import { online } from '@/net/online'
import { formatBytes } from './fileKind'

/** 上传面板里的一行：单独成组件，进度更新只重渲染这一行 */
const props = defineProps<{ item: UploadItem }>()
const { t } = useI18n()
const uploads = useDriveUploadsStore()

function statusText(): string {
  const i = props.item
  switch (i.status) {
    case 'queued': return online.value ? t('drive.upQueued') : t('chat.queued')
    case 'uploading': return `${Math.round(i.progress * 100)}%`
    case 'paused': return t('drive.upPaused')
    case 'registering': return t('drive.upSaving')
    case 'done': return t('drive.upDone')
    case 'failed': return i.error ?? t('drive.upFailed')
  }
}
</script>

<template>
  <li class="px-3 py-2 text-sm" style="border-bottom: 1px solid var(--pc-border)" :data-upload-status="item.status">
    <div class="flex items-center gap-2">
      <span class="min-w-0 flex-1 truncate" :title="item.name">{{ item.name }}</span>
      <span class="shrink-0 text-xs" style="color: var(--pc-text-muted)">{{ formatBytes(item.file.size) }}</span>
      <el-button v-if="item.status === 'uploading' || item.status === 'queued'" size="small" text :icon="VideoPause" :aria-label="t('drive.upPause')" @click="uploads.pause(item.id)" />
      <el-button v-else-if="item.status === 'paused'" size="small" text :icon="VideoPlay" :aria-label="t('drive.upResume')" @click="uploads.resume(item.id)" />
      <el-button v-else-if="item.status === 'failed'" size="small" text :icon="RefreshRight" :aria-label="t('common.retry')" @click="uploads.retry(item.id)" />
      <el-button v-if="item.status !== 'done'" size="small" text :icon="Close" :aria-label="t('common.cancel')" @click="uploads.cancel(item.id)" />
    </div>
    <el-progress
      v-if="item.status !== 'done'"
      :percentage="Math.round(item.progress * 100)"
      :status="item.status === 'failed' ? 'exception' : undefined"
      :show-text="false"
      :stroke-width="4"
    />
    <div class="text-xs" :style="item.status === 'failed' ? 'color: #d4483b' : 'color: var(--pc-text-muted)'">{{ statusText() }}</div>
  </li>
</template>
