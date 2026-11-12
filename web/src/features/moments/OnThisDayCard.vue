<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { OnThisDayDto } from '@/stores/moments'
import { useFamilyStore } from '@/stores/family'
import { openPreview, type PreviewItem } from '@/features/drive/preview'
import MediaTile from './MediaTile.vue'

/** 那年今天：每一年一行，最多 4 张缩略图（多的显示 +N），以及那天发的动态 */
const props = defineProps<{ data: OnThisDayDto }>()
const { t } = useI18n()
const family = useFamilyStore()

const years = computed(() => props.data.years)

function preview(year: OnThisDayDto['years'][number], index: number): void {
  const items: PreviewItem[] = year.photos.map((p) => ({ blobId: p.blobId, name: p.name ?? '', mime: p.mime }))
  openPreview(items[index]!, items)
}

function snippet(text: string | null | undefined): string {
  const s = (text ?? '').replace(/\s+/g, ' ').trim()
  return s.length > 40 ? s.slice(0, 40) + '…' : s || t('moments.photosOnly')
}
</script>

<template>
  <section v-if="years.length" class="rounded-xl p-4" style="background: var(--pc-primary-soft)" data-testid="on-this-day">
    <h2 class="mb-2 font-semibold">🕰 {{ t('moments.onThisDay') }}</h2>
    <div v-for="y in years" :key="y.year" class="mb-3 last:mb-0" data-testid="on-this-day-year" :data-year="y.year">
      <div class="mb-1 text-sm" style="color: var(--pc-text-muted)">{{ t('moments.yearsAgo', { n: y.yearsAgo, year: y.year }) }}</div>
      <ul v-if="y.photos.length" class="grid max-w-sm grid-cols-4 gap-1">
        <li v-for="(p, i) in y.photos.slice(0, 4)" :key="p.blobId" class="relative aspect-square overflow-hidden rounded">
          <MediaTile :blob-id="p.blobId" :mime="p.mime" :duration-seconds="p.durationSeconds" @open="preview(y, i)">
            <span
              v-if="i === 3 && y.photoCount > 4"
              class="pointer-events-none absolute inset-0 flex items-center justify-center text-lg font-semibold text-white"
              style="background: rgba(0, 0, 0, 0.45)"
            >+{{ y.photoCount - 3 }}</span>
          </MediaTile>
        </li>
      </ul>
      <ul v-if="y.posts.length" class="mt-1 text-sm">
        <li v-for="p in y.posts" :key="p.id">
          <router-link :to="`/moments/posts/${p.id}`" class="underline-offset-2 hover:underline" data-testid="on-this-day-post">
            {{ family.nameOf(p.authorId) }}：{{ snippet(p.text) }}
          </router-link>
        </li>
      </ul>
    </div>
  </section>
</template>
