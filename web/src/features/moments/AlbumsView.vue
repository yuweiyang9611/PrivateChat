<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus } from '@element-plus/icons-vue'
import { useMomentsStore } from '@/stores/moments'
import { blobUrl } from '@/upload'
import { monthTitle } from './format'

/** 相册：全家共享的手动相册 + 按月自动相册 */
const { t } = useI18n()
const router = useRouter()
const moments = useMomentsStore()
const failed = ref(false)
const broken = ref(new Set<string>())

async function load(): Promise<void> {
  failed.value = false
  try {
    await moments.loadAlbums()
  } catch {
    failed.value = true
  }
}

async function create(): Promise<void> {
  let title: string
  try {
    const r = await ElMessageBox.prompt(t('moments.albumTitlePrompt'), t('moments.newAlbum'), {
      confirmButtonText: t('common.confirm'),
      cancelButtonText: t('common.cancel'),
      inputValidator: (v: string) => (v.trim().length > 0 && v.trim().length <= 60) || t('moments.albumTitleInvalid'),
    })
    title = (r as { value: string }).value.trim()
  } catch {
    return
  }
  try {
    const a = await moments.createAlbum(title)
    void router.push(`/moments/albums/${a.id}`)
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

onMounted(load)
</script>

<template>
  <div class="h-full overflow-y-auto" data-testid="albums">
    <div class="mx-auto max-w-4xl p-3">
      <div class="mb-2 flex items-center justify-between">
        <h2 class="font-semibold">{{ t('moments.albums') }}</h2>
        <el-button size="small" :icon="Plus" data-testid="album-new" @click="create">{{ t('moments.newAlbum') }}</el-button>
      </div>
      <p v-if="failed" class="py-4 text-center text-xs">
        <button type="button" class="underline" @click="load">{{ t('drive.loadFailedRetry') }}</button>
      </p>
      <el-skeleton v-else-if="!moments.albums" animated :rows="3" />
      <template v-else>
        <p v-if="!moments.albums.albums.length" class="mb-4 text-sm" style="color: var(--pc-text-muted)">{{ t('moments.noAlbums') }}</p>
        <ul class="mb-6 grid gap-3" style="grid-template-columns: repeat(auto-fill, minmax(140px, 1fr))">
          <li v-for="a in moments.albums.albums" :key="a.id" data-testid="album-card" :data-album="a.id">
            <router-link :to="`/moments/albums/${a.id}`" class="block">
              <div class="mb-1 flex aspect-square items-center justify-center overflow-hidden rounded-lg" style="background: var(--pc-border)">
                <img
                  v-if="a.coverBlobId && !broken.has(a.coverBlobId)"
                  :src="blobUrl(a.coverBlobId, { v: 'thumb-320' })"
                  alt=""
                  loading="lazy"
                  class="h-full w-full object-cover"
                  @error="broken.add(a.coverBlobId!)"
                />
                <span v-else class="text-4xl">📷</span>
              </div>
              <div class="truncate text-sm font-medium" data-testid="album-title">{{ a.title }}</div>
              <div class="text-xs" style="color: var(--pc-text-muted)">{{ t('moments.itemCount', { n: a.itemCount }) }}</div>
            </router-link>
          </li>
        </ul>

        <h2 class="mb-2 font-semibold">{{ t('moments.byMonth') }}</h2>
        <p v-if="!moments.albums.months.length" class="text-sm" style="color: var(--pc-text-muted)">{{ t('drive.galleryEmpty') }}</p>
        <ul class="grid gap-3" style="grid-template-columns: repeat(auto-fill, minmax(120px, 1fr))">
          <li v-for="m in moments.albums.months" :key="m.month" data-testid="month-card" :data-month="m.month">
            <router-link :to="`/moments/months/${m.month}`" class="block">
              <div class="mb-1 flex aspect-square items-center justify-center overflow-hidden rounded-lg" style="background: var(--pc-border)">
                <img
                  v-if="!broken.has(m.coverBlobId)"
                  :src="blobUrl(m.coverBlobId, { v: 'thumb-320' })"
                  alt=""
                  loading="lazy"
                  class="h-full w-full object-cover"
                  @error="broken.add(m.coverBlobId)"
                />
                <span v-else class="text-4xl">🗓️</span>
              </div>
              <div class="text-sm font-medium">{{ monthTitle(m.month) }}</div>
              <div class="text-xs" style="color: var(--pc-text-muted)">{{ t('moments.itemCount', { n: m.count }) }}</div>
            </router-link>
          </li>
        </ul>
      </template>
    </div>
  </div>
</template>
