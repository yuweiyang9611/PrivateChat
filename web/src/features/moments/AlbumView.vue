<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ArrowLeft } from '@element-plus/icons-vue'
import { MomentsError, useMomentsStore, type AlbumDto, type AlbumItemDto } from '@/stores/moments'
import type { GalleryItemDto } from '@/stores/drive'
import { useSessionStore } from '@/stores/session'
import { useFamilyStore } from '@/stores/family'
import { uploadFile } from '@/upload'
import { openPreview, type PreviewItem } from '@/features/drive/preview'
import MediaTile from './MediaTile.vue'
import MediaPicker from './MediaPicker.vue'

/** 一个相册：全家都能看、都能往里加；创建人 / 管理员能改名、设封面、删除；加的人还能移出自己加的 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const moments = useMomentsStore()
const session = useSessionStore()
const family = useFamilyStore()

const id = computed(() => String(route.params.id ?? ''))
const album = ref<AlbumDto | null>(null)
const items = ref<AlbumItemDto[]>([])
const next = ref<{ before: string; beforeId: string } | null>(null)
const state = ref<'loading' | 'ok' | 'gone' | 'error'>('loading')
const loadingMore = ref(false)
const managing = ref(false)
const pickerOpen = ref(false)
const uploading = ref<{ done: number; total: number } | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)

const me = computed(() => session.user?.id)
const canManage = computed(() => !!album.value && (album.value.createdBy === me.value || session.isAdmin))
const canRemove = (i: AlbumItemDto): boolean => i.addedBy === me.value || canManage.value

async function load(): Promise<void> {
  try {
    const page = await moments.albumItems(id.value)
    album.value = page.album
    items.value = page.items
    next.value = page.nextBefore && page.nextBeforeId ? { before: page.nextBefore, beforeId: page.nextBeforeId } : null
    state.value = 'ok'
  } catch (e) {
    state.value = e instanceof MomentsError && e.status === 404 ? 'gone' : album.value ? 'ok' : 'error'
  }
}

async function loadMore(): Promise<void> {
  if (!next.value || loadingMore.value) return
  loadingMore.value = true
  try {
    const page = await moments.albumItems(id.value, next.value.before, next.value.beforeId)
    const have = new Set(items.value.map((i) => i.blobId))
    items.value.push(...page.items.filter((i) => !have.has(i.blobId)))
    next.value = page.nextBefore && page.nextBeforeId ? { before: page.nextBefore, beforeId: page.nextBeforeId } : null
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    loadingMore.value = false
  }
}

watch(id, () => {
  state.value = 'loading'
  album.value = null
  items.value = []
  managing.value = false
  void family.load()
  void load()
}, { immediate: true })

// 家人往里加了照片 / 改了名 / 删了相册
watch(() => moments.albumVersion[id.value], (v, old) => {
  if (v !== undefined && v !== old) void load()
})

async function addPicked(picked: GalleryItemDto[]): Promise<void> {
  await addBlobs(id.value, picked.map((p) => p.blobId))
}

/** albumId 在开始时就定下来：上传要一会儿，期间用户可能已经打开了别的相册 */
async function addBlobs(albumId: string, blobIds: string[]): Promise<void> {
  if (!blobIds.length || !albumId) return
  try {
    const added = await moments.addToAlbum(albumId, blobIds)
    ElMessage.success(added ? t('moments.addedToAlbum', { n: added }) : t('moments.alreadyInAlbum'))
    if (id.value === albumId) await load()
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

async function uploadFiles(list: FileList | null): Promise<void> {
  const albumId = id.value
  const files = Array.from(list ?? []).filter((f) => f.type.startsWith('image/') || f.type.startsWith('video/'))
  if (fileInput.value) fileInput.value.value = ''
  if (!files.length) return
  uploading.value = { done: 0, total: files.length }
  const ids: string[] = []
  let failedCount = 0
  for (const f of files) {
    try {
      ids.push(await uploadFile(f).done)
    } catch {
      failedCount++
    }
    uploading.value = { done: uploading.value.done + 1, total: files.length }
  }
  uploading.value = null
  if (failedCount) ElMessage.warning(t('moments.someUploadsFailed', { n: failedCount }))
  await addBlobs(albumId, ids)
}

async function rename(): Promise<void> {
  if (!album.value) return
  let title: string
  try {
    const r = await ElMessageBox.prompt(t('moments.albumTitlePrompt'), t('moments.renameAlbum'), {
      inputValue: album.value.title,
      confirmButtonText: t('common.confirm'),
      cancelButtonText: t('common.cancel'),
      inputValidator: (v: string) => (v.trim().length > 0 && v.trim().length <= 60) || t('moments.albumTitleInvalid'),
    })
    title = (r as { value: string }).value.trim()
  } catch {
    return
  }
  try {
    album.value = await moments.patchAlbum(id.value, { title })
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

async function removeAlbum(): Promise<void> {
  try {
    await ElMessageBox.confirm(t('moments.deleteAlbumConfirm'), { type: 'warning', confirmButtonText: t('moments.delete'), cancelButtonText: t('common.cancel') })
  } catch {
    return
  }
  try {
    await moments.deleteAlbum(id.value)
    void router.push('/moments/albums')
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

async function setCover(i: AlbumItemDto): Promise<void> {
  try {
    album.value = await moments.patchAlbum(id.value, { coverBlobId: i.blobId })
    ElMessage.success(t('moments.coverSet'))
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

async function removeItem(i: AlbumItemDto): Promise<void> {
  try {
    album.value = await moments.removeFromAlbum(id.value, i.blobId)
    items.value = items.value.filter((x) => x.blobId !== i.blobId)
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

function preview(i: AlbumItemDto): void {
  const list: PreviewItem[] = items.value.map((x) => ({ blobId: x.blobId, name: '', mime: x.mime }))
  openPreview(list.find((x) => x.blobId === i.blobId)!, list)
}
</script>

<template>
  <div class="h-full overflow-y-auto" data-testid="album-view">
    <div class="mx-auto max-w-4xl p-3">
      <div class="mb-3 flex flex-wrap items-center gap-2">
        <el-button text :icon="ArrowLeft" @click="router.push('/moments/albums')">{{ t('moments.albums') }}</el-button>
        <template v-if="album">
          <h2 class="min-w-0 flex-1 truncate text-lg font-semibold" data-testid="album-heading">{{ album.title }}</h2>
          <span class="text-xs" style="color: var(--pc-text-muted)">
            {{ t('moments.itemCount', { n: album.itemCount }) }} · {{ t('moments.createdBy', { name: family.nameOf(album.createdBy) }) }}
          </span>
        </template>
      </div>

      <div v-if="album" class="mb-3 flex flex-wrap gap-2">
        <el-button size="small" type="primary" data-testid="album-add" @click="pickerOpen = true">{{ t('moments.addFromGallery') }}</el-button>
        <el-button size="small" :loading="!!uploading" data-testid="album-upload" @click="fileInput?.click()">
          {{ uploading ? t('moments.uploadingProgress', uploading) : t('moments.uploadToAlbum') }}
        </el-button>
        <el-button v-if="items.length" size="small" :type="managing ? 'warning' : 'default'" data-testid="album-manage" @click="managing = !managing">
          {{ managing ? t('moments.doneManaging') : t('moments.manage') }}
        </el-button>
        <el-button v-if="canManage" size="small" data-testid="album-rename" @click="rename">{{ t('moments.renameAlbum') }}</el-button>
        <el-button v-if="canManage" size="small" type="danger" plain data-testid="album-delete" @click="removeAlbum">{{ t('moments.deleteAlbum') }}</el-button>
        <input ref="fileInput" type="file" accept="image/*,video/*" multiple hidden data-testid="album-file" @change="uploadFiles(($event.target as HTMLInputElement).files)" />
      </div>

      <el-skeleton v-if="state === 'loading'" animated :rows="4" />
      <el-empty v-else-if="state === 'gone'" :description="t('moments.albumGone')" />
      <el-empty v-else-if="state === 'error'" :description="t('moments.loadFailed')" />
      <template v-else>
        <el-empty v-if="!items.length" :description="t('moments.albumEmpty')" />
        <ul class="grid gap-1" style="grid-template-columns: repeat(auto-fill, minmax(104px, 1fr))">
          <li v-for="i in items" :key="i.blobId" class="relative aspect-square overflow-hidden rounded" data-testid="album-item">
            <MediaTile :blob-id="i.blobId" :mime="i.mime" :duration-seconds="i.durationSeconds" @open="preview(i)">
              <span
                v-if="album?.coverBlobId === i.blobId"
                class="pointer-events-none absolute left-1 top-1 rounded px-1 text-[11px] text-white"
                style="background: var(--pc-primary)"
              >{{ t('moments.cover') }}</span>
              <div v-if="managing" class="absolute inset-x-0 bottom-0 flex justify-between gap-1 p-1" style="background: rgba(0, 0, 0, 0.5)">
                <button v-if="canManage" type="button" class="text-[11px] text-white" data-testid="album-set-cover" @click="setCover(i)">{{ t('moments.setCover') }}</button>
                <button v-if="canRemove(i)" type="button" class="ml-auto text-[11px] text-white" data-testid="album-remove-item" @click="removeItem(i)">{{ t('moments.removeFromAlbum') }}</button>
              </div>
            </MediaTile>
          </li>
        </ul>
        <div v-if="next" class="py-3 text-center text-xs">
          <button type="button" class="underline" :disabled="loadingMore" @click="loadMore">{{ loadingMore ? t('drive.loading') : t('moments.loadMore') }}</button>
        </div>
      </template>
    </div>
    <MediaPicker :open="pickerOpen" :max="500" :title="t('moments.addFromGallery')" @close="pickerOpen = false" @pick="addPicked" />
  </div>
</template>
