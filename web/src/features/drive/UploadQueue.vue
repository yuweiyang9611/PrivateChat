<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowDown, ArrowUp } from '@element-plus/icons-vue'
import { useDriveUploadsStore, type UploadItem } from '@/stores/driveUploads'
import UploadRow from './UploadRow.vue'

/** 每种"成堆"的状态最多显示这么多行，其余折成"还有 N 个"：一次选几千张照片时面板不卡 */
const ROW_CAP = 50

const { t } = useI18n()
const uploads = useDriveUploadsStore()
const collapsed = ref(false)

const visible = computed(() => uploads.items.length > 0)
const percent = computed(() => Math.round(uploads.totalProgress * 100))

/** 正在传 / 暂停的全显示；排队、失败、已完成的各显示前 ROW_CAP 个，保持原顺序 */
const rows = computed(() => {
  const out: UploadItem[] = []
  const seen = { queued: 0, failed: 0, done: 0 }
  for (const i of uploads.items) {
    if (i.status === 'queued' || i.status === 'failed' || i.status === 'done') {
      if (seen[i.status]++ >= ROW_CAP) continue
    }
    out.push(i)
  }
  return out
})
const hidden = computed(() => uploads.items.length - rows.value.length)
</script>

<template>
  <div
    v-if="visible"
    class="fixed z-[2000] w-[min(92vw,360px)] overflow-hidden rounded-xl shadow-lg"
    style="right: 12px; bottom: calc(72px + env(safe-area-inset-bottom)); background: var(--pc-surface); border: 1px solid var(--pc-border)"
    data-testid="upload-queue"
  >
    <header class="flex items-center gap-2 px-3 py-2 text-sm" style="border-bottom: 1px solid var(--pc-border)">
      <span class="flex-1 font-medium">{{ uploads.unfinished > 0 ? t('drive.upTitle', { n: uploads.unfinished, p: percent }) : t('drive.upAllDone') }}</span>
      <el-button v-if="uploads.unfinished === 0" size="small" text @click="uploads.clearFinished()">{{ t('drive.upClear') }}</el-button>
      <el-button size="small" text :icon="collapsed ? ArrowUp : ArrowDown" @click="collapsed = !collapsed" />
    </header>
    <ul v-if="!collapsed" class="max-h-64 overflow-y-auto">
      <UploadRow v-for="i in rows" :key="i.id" :item="i" />
      <li v-if="hidden > 0" class="px-3 py-2 text-center text-xs" style="color: var(--pc-text-muted)" data-testid="upload-more">{{ t('drive.upMore', { n: hidden }) }}</li>
    </ul>
  </div>
</template>
