<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useDriveStore, type GalleryItemDto } from '@/stores/drive'
import MediaTile from './MediaTile.vue'

/**
 * 从时间线里挑照片 / 视频（自己能看到的：聊天里的、网盘里的、动态和相册里的），多选。
 * max：最多还能选几张（发动态时 9 减去已选的）。
 */
const props = defineProps<{ open: boolean; max: number; title?: string }>()
const emit = defineEmits<{ close: []; pick: [items: GalleryItemDto[]] }>()
const { t } = useI18n()
const drive = useDriveStore()

const items = ref<GalleryItemDto[]>([])
const next = ref<{ before: string | null; beforeId: string | null } | null>({ before: null, beforeId: null })
const loading = ref(false)
const failed = ref(false)
const picked = ref<string[]>([])

const visible = computed({ get: () => props.open, set: (v) => { if (!v) emit('close') } })

async function loadMore(): Promise<void> {
  if (loading.value || !next.value) return
  loading.value = true
  failed.value = false
  try {
    const page = await drive.gallery(next.value.before, next.value.beforeId, 60)
    items.value.push(...page.items)
    next.value = page.nextBefore ? { before: page.nextBefore, beforeId: page.nextBeforeId ?? null } : null
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
}

watch(() => props.open, (o) => {
  if (!o) return
  items.value = []
  picked.value = []
  next.value = { before: null, beforeId: null }
  void loadMore()
}, { immediate: true })

function toggle(id: string): void {
  const i = picked.value.indexOf(id)
  if (i >= 0) picked.value.splice(i, 1)
  else if (picked.value.length < props.max) picked.value.push(id)
}

function confirm(): void {
  const byId = new Map(items.value.map((i) => [i.blobId, i]))
  emit('pick', picked.value.map((id) => byId.get(id)!).filter(Boolean))
  emit('close')
}
</script>

<template>
  <el-dialog v-model="visible" :title="title ?? t('moments.pickFromGallery')" width="min(96vw, 720px)" append-to-body data-testid="media-picker">
    <div class="max-h-[60vh] overflow-y-auto">
      <el-empty v-if="!loading && !failed && !items.length" :description="t('drive.galleryEmpty')" />
      <ul class="grid gap-1" style="grid-template-columns: repeat(auto-fill, minmax(96px, 1fr))">
        <li v-for="i in items" :key="i.blobId" class="relative aspect-square overflow-hidden rounded" data-testid="picker-item">
          <MediaTile :blob-id="i.blobId" :mime="i.mime" :duration-seconds="i.durationSeconds" @open="toggle(i.blobId)">
            <span
              class="pointer-events-none absolute right-1 top-1 flex h-6 w-6 items-center justify-center rounded-full text-xs font-semibold text-white"
              :style="picked.includes(i.blobId) ? 'background: var(--pc-primary)' : 'background: rgba(0,0,0,0.35); border: 2px solid #fff'"
            >{{ picked.includes(i.blobId) ? picked.indexOf(i.blobId) + 1 : '' }}</span>
          </MediaTile>
        </li>
      </ul>
      <div class="py-2 text-center text-xs" style="color: var(--pc-text-muted)">
        <span v-if="loading">{{ t('drive.loading') }}</span>
        <button v-else-if="failed" type="button" class="underline" @click="loadMore">{{ t('drive.loadFailedRetry') }}</button>
        <button v-else-if="next" type="button" class="underline" @click="loadMore">{{ t('moments.loadMore') }}</button>
      </div>
    </div>
    <template #footer>
      <span class="mr-3 text-xs" style="color: var(--pc-text-muted)">{{ t('moments.pickedCount', { n: picked.length, max }) }}</span>
      <el-button @click="emit('close')">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :disabled="!picked.length" data-testid="picker-confirm" @click="confirm">{{ t('common.confirm') }}</el-button>
    </template>
  </el-dialog>
</template>
