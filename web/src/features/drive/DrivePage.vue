<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { useDriveStore } from '@/stores/drive'
import { formatBytes } from './fileKind'
import PreviewHost from './PreviewHost.vue'
import UploadQueue from './UploadQueue.vue'

/** 网盘外壳：桌面左侧分区导航 + 用量，手机顶部横向分区；内容由子路由渲染 */
const { t } = useI18n()
const route = useRoute()
const drive = useDriveStore()

const sections = computed(() => [
  { key: 'my', to: '/drive/my', label: t('drive.my'), icon: '🗂️' },
  { key: 'family', to: '/drive/family', label: t('drive.family'), icon: '🏠' },
  { key: 'gallery', to: '/drive/gallery', label: t('drive.gallery'), icon: '🖼️' },
  { key: 'shared', to: '/drive/shared', label: t('drive.shared'), icon: '🤝' },
  { key: 'trash', to: '/drive/trash', label: t('drive.trash'), icon: '🗑️' },
])
const active = computed(() => (route.meta.driveSection as string | undefined) ?? 'my')

const usage = computed(() => {
  const o = drive.overview
  if (!o) return null
  const mine = o.spaces.find((s) => s.type === 'Personal')
  const fam = o.spaces.find((s) => s.type === 'Family')
  return {
    mine: mine ? formatBytes(mine.usedBytes) + (mine.quotaBytes ? ` / ${formatBytes(mine.quotaBytes)}` : '') : '',
    family: fam ? formatBytes(fam.usedBytes) : '',
    free: o.diskTotalBytes ? formatBytes(o.diskFreeBytes) : '',
    ratio: o.diskTotalBytes ? 1 - o.diskFreeBytes / o.diskTotalBytes : 0,
  }
})

onMounted(() => void drive.loadOverview())
</script>

<template>
  <div class="flex h-full min-h-0">
    <aside class="hidden w-52 shrink-0 flex-col gap-1 p-3 lg:flex" style="border-right: 1px solid var(--pc-border)">
      <router-link
        v-for="s in sections"
        :key="s.key"
        :to="s.to"
        class="flex items-center gap-2 rounded-lg px-3 py-2 text-sm"
        :style="active === s.key ? 'background: var(--pc-primary-soft); color: var(--pc-primary); font-weight: 600' : ''"
        :data-testid="`drive-nav-${s.key}`"
      >
        <span>{{ s.icon }}</span><span>{{ s.label }}</span>
      </router-link>
      <div v-if="usage" class="mt-auto text-xs" style="color: var(--pc-text-muted)" data-testid="drive-usage">
        <div>{{ t('drive.usageMine', { v: usage.mine }) }}</div>
        <div>{{ t('drive.usageFamily', { v: usage.family }) }}</div>
        <template v-if="usage.free">
          <el-progress :percentage="Math.round(usage.ratio * 100)" :show-text="false" :stroke-width="6" class="my-1" />
          <div>{{ t('drive.diskFree', { v: usage.free }) }}</div>
        </template>
      </div>
    </aside>

    <div class="flex min-w-0 flex-1 flex-col">
      <nav class="flex shrink-0 gap-1 overflow-x-auto px-2 py-2 lg:hidden" style="border-bottom: 1px solid var(--pc-border)">
        <router-link
          v-for="s in sections"
          :key="s.key"
          :to="s.to"
          class="shrink-0 rounded-full px-3 py-1 text-sm"
          :style="active === s.key ? 'background: var(--pc-primary); color: #fff' : 'background: var(--pc-border)'"
        >
          {{ s.label }}
        </router-link>
      </nav>
      <div class="min-h-0 flex-1">
        <router-view />
      </div>
    </div>

    <UploadQueue />
    <PreviewHost />
  </div>
</template>
