<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter, useRoute } from 'vue-router'
import { ArrowLeft } from '@element-plus/icons-vue'
import { useMomentsStore } from '@/stores/moments'
import type { GalleryItemDto } from '@/stores/drive'
import { openPreview, type PreviewItem } from '@/features/drive/preview'
import MediaTile from './MediaTile.vue'
import { monthTitle } from './format'

/** 按月相册：时间线里这个月的照片和视频（不落库，直接按时间范围查） */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const moments = useMomentsStore()

const month = computed(() => String(route.params.month ?? ''))
const items = ref<GalleryItemDto[]>([])
const next = ref<{ before: string; beforeId: string | null } | null>(null)
/** 这个月在家庭时区下的起点（服务器给的） */
let since = ''
const loading = ref(false)
const failed = ref(false)

async function loadMore(): Promise<void> {
  if (!next.value || loading.value) return
  loading.value = true
  failed.value = false
  try {
    const page = await moments.monthItems(since, next.value.before, next.value.beforeId)
    items.value.push(...page.items)
    next.value = page.nextBefore ? { before: page.nextBefore, beforeId: page.nextBeforeId ?? null } : null
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
}

watch(month, async (m) => {
  if (!/^\d{4}-\d{2}$/.test(m)) return
  items.value = []
  next.value = null
  failed.value = false
  // 月份的起止由服务器按家庭时区给出（直接打开链接时先取一次相册总览）
  if (!moments.albums) {
    loading.value = true
    await moments.loadAlbums().catch(() => { failed.value = true })
    loading.value = false
  }
  if (month.value !== m) return
  const range = moments.albums?.months.find((x) => x.month === m)
  if (!range) return
  since = range.since
  next.value = { before: range.before, beforeId: null }
  void loadMore()
}, { immediate: true })

function preview(i: GalleryItemDto): void {
  const list: PreviewItem[] = items.value.map((x) => ({ blobId: x.blobId, name: x.name ?? '', mime: x.mime }))
  openPreview(list.find((x) => x.blobId === i.blobId)!, list)
}
</script>

<template>
  <div class="h-full overflow-y-auto" data-testid="month-view">
    <div class="mx-auto max-w-4xl p-3">
      <div class="mb-3 flex items-center gap-2">
        <el-button text :icon="ArrowLeft" @click="router.push('/moments/albums')">{{ t('moments.albums') }}</el-button>
        <h2 class="text-lg font-semibold">{{ monthTitle(month) }}</h2>
        <span class="text-xs" style="color: var(--pc-text-muted)">{{ t('moments.itemCount', { n: items.length }) }}{{ next ? '+' : '' }}</span>
      </div>
      <el-empty v-if="!loading && !failed && !items.length" :description="t('drive.galleryEmpty')" />
      <ul class="grid gap-1" style="grid-template-columns: repeat(auto-fill, minmax(104px, 1fr))">
        <li v-for="i in items" :key="i.blobId" class="aspect-square overflow-hidden rounded" data-testid="month-item">
          <MediaTile :blob-id="i.blobId" :mime="i.mime" :duration-seconds="i.durationSeconds" @open="preview(i)" />
        </li>
      </ul>
      <div class="py-3 text-center text-xs" style="color: var(--pc-text-muted)">
        <span v-if="loading">{{ t('drive.loading') }}</span>
        <button v-else-if="failed" type="button" class="underline" @click="loadMore">{{ t('drive.loadFailedRetry') }}</button>
        <button v-else-if="next" type="button" class="underline" @click="loadMore">{{ t('moments.loadMore') }}</button>
      </div>
    </div>
  </div>
</template>
