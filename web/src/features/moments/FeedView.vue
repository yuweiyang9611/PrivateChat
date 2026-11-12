<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useMomentsStore } from '@/stores/moments'
import { useFamilyStore } from '@/stores/family'
import { onOnline } from '@/net/online'
import OnThisDayCard from './OnThisDayCard.vue'
import PostCard from './PostCard.vue'

/** 时光首页：那年今天 + 全家的动态（新的在前，滚到底自动加载更早的） */
const { t } = useI18n()
const moments = useMomentsStore()
const family = useFamilyStore()
const scroller = ref<HTMLElement | null>(null)
const sentinel = ref<HTMLElement | null>(null)
let observer: IntersectionObserver | null = null

/** 加载完一页后哨兵仍在视野里（一页没填满屏幕）时接着加载 */
function rearm(): void {
  if (!observer || !sentinel.value) return
  observer.unobserve(sentinel.value)
  observer.observe(sentinel.value)
}

async function more(): Promise<void> {
  await moments.loadMore()
  await nextTick()
  if (moments.next && !moments.error) rearm()
}

watch(() => moments.posts.length, () => void nextTick(rearm))

let offOnline: (() => void) | null = null
onMounted(async () => {
  void family.load()
  observer = new IntersectionObserver((entries) => {
    if (entries.some((e) => e.isIntersecting) && moments.loaded) void more()
  }, { root: scroller.value, rootMargin: '600px' })
  if (sentinel.value) observer.observe(sentinel.value)
  // 每次进来都刷新第一页（离开期间可能错过了实时事件），那年今天每天变一次
  void moments.loadOnThisDay()
  await moments.loadFirst()
  await nextTick()
  rearm()
  offOnline = onOnline(() => {
    if (moments.error) void moments.loadFirst()
  })
})
onBeforeUnmount(() => {
  observer?.disconnect()
  offOnline?.()
})
</script>

<template>
  <div ref="scroller" class="h-full overflow-y-auto" data-testid="moments-feed">
    <div class="mx-auto flex max-w-2xl flex-col gap-3 p-3">
      <OnThisDayCard v-if="moments.onThisDay" :data="moments.onThisDay" />
      <PostCard v-for="p in moments.posts" :key="p.id" :post="p" />
      <el-empty v-if="moments.loaded && !moments.posts.length && !moments.loading" :description="t('moments.empty')" />
      <div ref="sentinel" class="h-6" />
      <p v-if="moments.loading" class="pb-4 text-center text-xs" style="color: var(--pc-text-muted)">{{ t('drive.loading') }}</p>
      <p v-else-if="moments.error" class="pb-4 text-center text-xs" data-testid="moments-error">
        {{ moments.error }} ·
        <button type="button" class="underline" @click="moments.loaded ? more() : moments.loadFirst()">{{ t('common.retry') }}</button>
      </p>
      <p v-else-if="moments.loaded && moments.posts.length && !moments.next" class="pb-4 text-center text-xs" style="color: var(--pc-text-muted)">
        {{ t('moments.end') }}
      </p>
    </div>
  </div>
</template>
