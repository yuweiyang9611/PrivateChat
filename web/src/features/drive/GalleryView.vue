<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useDriveStore, type GalleryItemDto } from '@/stores/drive'
import { blobUrl } from '@/upload'
import { formatDuration, monthLabel } from './fileKind'
import { openPreview, type PreviewItem } from './preview'

/**
 * 相册时间线：所有能看到的照片 / 视频（聊天里发的、网盘里存的），按拍摄时间倒序、按月分组，滚到底自动加载下一页。
 */
const { t } = useI18n()
const drive = useDriveStore()
const items = ref<GalleryItemDto[]>([])
const next = ref<{ before: string | null; beforeId: string | null } | null>({ before: null, beforeId: null })
const loading = ref(false)
const failed = ref(false)
const broken = reactive(new Set<string>())
const sentinel = ref<HTMLElement | null>(null)
const scroller = ref<HTMLElement | null>(null)

const groups = computed(() => {
  const out: { label: string; items: GalleryItemDto[] }[] = []
  for (const it of items.value) {
    const label = monthLabel(it.sortAt)
    const last = out[out.length - 1]
    if (last?.label === label) last.items.push(it)
    else out.push({ label, items: [it] })
  }
  return out
})

async function loadMore(): Promise<void> {
  if (loading.value || !next.value) return
  loading.value = true
  failed.value = false
  let ok = false
  try {
    const page = await drive.gallery(next.value.before, next.value.beforeId, 60)
    items.value.push(...page.items)
    next.value = page.nextBefore ? { before: page.nextBefore, beforeId: page.nextBeforeId ?? null } : null
    ok = true
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
  if (ok && next.value) {
    // IntersectionObserver 只在"进入 / 离开"时回调：一页没填满屏幕时哨兵一直可见，不会再回调。
    // 重新 observe 一定会立刻给一次当前状态，还看得见就接着加载下一页
    await nextTick()
    rearm()
  }
}

function rearm(): void {
  if (!observer || !sentinel.value) return
  observer.unobserve(sentinel.value)
  observer.observe(sentinel.value)
}

function asPreview(i: GalleryItemDto): PreviewItem {
  return { blobId: i.blobId, name: i.name ?? '', mime: i.mime }
}

function open(i: GalleryItemDto): void {
  openPreview(asPreview(i), items.value.map(asPreview))
}

let observer: IntersectionObserver | null = null
onMounted(() => {
  // root 设成滚动容器，rootMargin 才是"离底部 600 px 就预加载"
  observer = new IntersectionObserver((entries) => {
    if (entries.some((e) => e.isIntersecting)) void loadMore()
  }, { root: scroller.value, rootMargin: '600px' })
  if (sentinel.value) observer.observe(sentinel.value)
  void loadMore()
})
onBeforeUnmount(() => observer?.disconnect())
</script>

<template>
  <div ref="scroller" class="h-full overflow-y-auto p-3" data-testid="gallery">
    <el-empty v-if="!loading && !failed && !items.length && !next" :description="t('drive.galleryEmpty')" />
    <section v-for="g in groups" :key="g.label" class="mb-4">
      <h3 class="sticky top-0 z-[1] mb-2 py-1 text-sm font-semibold" style="background: var(--pc-bg)">{{ g.label }}</h3>
      <ul class="grid gap-1" style="grid-template-columns: repeat(auto-fill, minmax(104px, 1fr))">
        <li v-for="i in g.items" :key="i.blobId" class="relative aspect-square overflow-hidden rounded" style="background: var(--pc-border)" :data-blob="i.blobId">
          <button type="button" class="h-full w-full" :aria-label="i.name ?? ''" @click="open(i)">
            <img v-if="!broken.has(i.blobId)" :src="blobUrl(i.blobId, { v: 'thumb-320' })" alt="" loading="lazy" class="h-full w-full object-cover" @error="broken.add(i.blobId)" />
            <span v-else class="text-3xl">{{ i.mime.startsWith('video/') ? '🎞️' : '🖼️' }}</span>
          </button>
          <span v-if="i.mime.startsWith('video/')" class="pointer-events-none absolute bottom-1 right-1 rounded px-1 text-[11px] text-white" style="background: rgba(0, 0, 0, 0.55)">
            ▶ {{ formatDuration(i.durationSeconds) }}
          </span>
        </li>
      </ul>
    </section>
    <div ref="sentinel" class="h-8" />
    <p v-if="loading" class="py-2 text-center text-xs" style="color: var(--pc-text-muted)">{{ t('drive.loading') }}</p>
    <p v-else-if="failed" class="py-2 text-center text-xs">
      <button type="button" class="underline" @click="loadMore">{{ t('drive.loadFailedRetry') }}</button>
    </p>
  </div>
</template>
