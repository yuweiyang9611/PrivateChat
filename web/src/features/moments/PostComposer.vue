<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Picture, Plus } from '@element-plus/icons-vue'
import { uploadFile, type UploadHandle } from '@/upload'
import { useMomentsStore, type PostVisibility } from '@/stores/moments'
import type { GalleryItemDto } from '@/stores/drive'
import MediaTile from './MediaTile.vue'
import MediaPicker from './MediaPicker.vue'
import { MAX_MEDIA, MAX_POST_TEXT } from './format'

/** 发动态：文字 + 至多 9 张照片 / 视频（现拍现传，或从时间线里挑），可见范围"家人" / "仅自己" */
defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()
const moments = useMomentsStore()

interface Draft {
  key: string
  mime: string
  /** 本地文件的预览地址（上传中显示） */
  localUrl?: string
  file?: File
  handle?: UploadHandle
  blobId?: string
  durationSeconds?: number | null
  progress: number
  status: 'uploading' | 'done' | 'failed'
}

const text = ref('')
const visibility = ref<PostVisibility>('Family')
const drafts = ref<Draft[]>([])
const publishing = ref(false)
const pickerOpen = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)
const brokenLocal = reactive(new Set<string>())
let seq = 0

const remaining = computed(() => MAX_MEDIA - drafts.value.length)
const uploading = computed(() => drafts.value.some((d) => d.status === 'uploading'))
const failed = computed(() => drafts.value.some((d) => d.status === 'failed'))
const canPublish = computed(() => !publishing.value && !uploading.value && !failed.value && (text.value.trim().length > 0 || drafts.value.length > 0))
const dirty = computed(() => text.value.trim().length > 0 || drafts.value.length > 0)


function start(d: Draft): void {
  d.status = 'uploading'
  d.progress = 0
  d.handle = uploadFile(d.file!, { onProgress: (r) => { d.progress = r } })
  d.handle.done.then((id) => {
    d.blobId = id
    d.status = 'done'
  }).catch(() => {
    // 被移除的不算失败
    if (drafts.value.includes(d)) d.status = 'failed'
  })
}

function addFiles(list: FileList | null): void {
  const files = Array.from(list ?? []).filter((f) => f.type.startsWith('image/') || f.type.startsWith('video/'))
  if (list && files.length < list.length) ElMessage.warning(t('moments.onlyPhotosVideos'))
  if (files.length > remaining.value) ElMessage.warning(t('moments.maxMedia', { n: MAX_MEDIA }))
  for (const file of files.slice(0, Math.max(0, remaining.value))) {
    const d = reactive<Draft>({ key: `f${seq++}`, mime: file.type, file, localUrl: URL.createObjectURL(file), progress: 0, status: 'uploading' })
    drafts.value.push(d)
    start(d)
  }
  if (fileInput.value) fileInput.value.value = ''
}

function addPicked(items: GalleryItemDto[]): void {
  const have = new Set(drafts.value.map((d) => d.blobId))
  for (const i of items.filter((x) => !have.has(x.blobId)).slice(0, Math.max(0, remaining.value))) {
    drafts.value.push({ key: `g${seq++}`, mime: i.mime, blobId: i.blobId, durationSeconds: i.durationSeconds, progress: 1, status: 'done' })
  }
}

function removeDraft(d: Draft): void {
  drafts.value = drafts.value.filter((x) => x !== d)
  if (d.status === 'uploading') void d.handle?.abort()
  if (d.localUrl) URL.revokeObjectURL(d.localUrl)
}

function reset(): void {
  for (const d of drafts.value) {
    if (d.status === 'uploading') void d.handle?.abort()
    if (d.localUrl) URL.revokeObjectURL(d.localUrl)
  }
  drafts.value = []
  text.value = ''
  visibility.value = 'Family'
  brokenLocal.clear()
}

async function close(): Promise<void> {
  if (publishing.value) return
  if (dirty.value) {
    try {
      await ElMessageBox.confirm(t('moments.discardConfirm'), { type: 'warning', confirmButtonText: t('moments.discard'), cancelButtonText: t('moments.keepEditing') })
    } catch {
      return
    }
  }
  reset()
  emit('close')
}

/** 点 ✕ / 按 Esc：交给 close() 决定（有内容先确认），确认后才通知外面关掉；不确认就原样留着 */
function beforeClose(): void {
  void close()
}

// 返回手势 / 切走路由直接卸载：别让上传在后台接着跑，本地预览地址也要释放
onBeforeUnmount(reset)

async function publish(): Promise<void> {
  if (!canPublish.value) return
  publishing.value = true
  try {
    await moments.create(text.value.trim() || null, visibility.value, drafts.value.map((d) => d.blobId!))
    reset()
    emit('close')
    ElMessage.success(t('moments.published'))
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    publishing.value = false
  }
}
</script>

<template>
  <el-dialog :model-value="open" :before-close="beforeClose" :title="t('moments.newPost')" width="min(96vw, 560px)" append-to-body :close-on-click-modal="false" data-testid="post-composer">
    <el-input
      v-model="text"
      type="textarea"
      :autosize="{ minRows: 3, maxRows: 10 }"
      :maxlength="MAX_POST_TEXT"
      :placeholder="t('moments.textPlaceholder')"
      data-testid="composer-text"
    />

    <ul class="mt-3 grid gap-2" style="grid-template-columns: repeat(3, minmax(0, 1fr))">
      <li v-for="d in drafts" :key="d.key" class="relative aspect-square overflow-hidden rounded" style="background: var(--pc-border)" data-testid="composer-media">
        <template v-if="d.localUrl">
          <video v-if="d.mime.startsWith('video/')" :src="d.localUrl" muted playsinline preload="metadata" class="h-full w-full object-cover" />
          <img v-else-if="!brokenLocal.has(d.key)" :src="d.localUrl" alt="" class="h-full w-full object-cover" @error="brokenLocal.add(d.key)" />
          <span v-else class="flex h-full items-center justify-center text-3xl">🖼️</span>
        </template>
        <MediaTile v-else-if="d.blobId" :blob-id="d.blobId" :mime="d.mime" :duration-seconds="d.durationSeconds" />
        <div v-if="d.status === 'uploading'" class="absolute inset-x-0 bottom-0 h-1" style="background: rgba(0, 0, 0, 0.3)">
          <div class="h-full" :style="{ width: `${Math.round(d.progress * 100)}%`, background: 'var(--pc-primary)' }" />
        </div>
        <button
          v-if="d.status === 'failed'"
          type="button"
          class="absolute inset-0 flex items-center justify-center text-sm text-white"
          style="background: rgba(0, 0, 0, 0.5)"
          @click="start(d)"
        >{{ t('moments.uploadFailedRetry') }}</button>
        <button
          type="button"
          class="absolute right-1 top-1 flex h-6 w-6 items-center justify-center rounded-full text-xs text-white"
          style="background: rgba(0, 0, 0, 0.55)"
          :aria-label="t('moments.removeMedia')"
          @click="removeDraft(d)"
        >✕</button>
      </li>
      <li v-if="remaining > 0" class="aspect-square">
        <button
          type="button"
          class="flex h-full w-full flex-col items-center justify-center gap-1 rounded text-xs"
          style="border: 1px dashed var(--pc-border); color: var(--pc-text-muted)"
          data-testid="composer-add"
          @click="fileInput?.click()"
        >
          <el-icon :size="24"><Plus /></el-icon>{{ t('moments.addPhotos') }}
        </button>
      </li>
    </ul>
    <input ref="fileInput" type="file" accept="image/*,video/*" multiple hidden data-testid="composer-file" @change="addFiles(($event.target as HTMLInputElement).files)" />

    <div class="mt-3 flex flex-wrap items-center gap-3">
      <el-button v-if="remaining > 0" size="small" :icon="Picture" data-testid="composer-pick" @click="pickerOpen = true">{{ t('moments.pickFromGallery') }}</el-button>
      <el-radio-group v-model="visibility" size="small" data-testid="composer-visibility">
        <el-radio-button value="Family">{{ t('moments.visibleFamily') }}</el-radio-button>
        <el-radio-button value="Private">🔒 {{ t('moments.visiblePrivate') }}</el-radio-button>
      </el-radio-group>
    </div>

    <template #footer>
      <el-button @click="close">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="publishing" :disabled="!canPublish" data-testid="composer-publish" @click="publish">
        {{ uploading ? t('moments.uploading') : t('moments.publish') }}
      </el-button>
    </template>

    <MediaPicker :open="pickerOpen" :max="remaining" @close="pickerOpen = false" @pick="addPicked" />
  </el-dialog>
</template>
