<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { useStorage } from '@vueuse/core'
import { ElMessage, ElMessageBox } from 'element-plus'
import { FolderAdd, Grid, List, Refresh, Upload } from '@element-plus/icons-vue'
import { atLeast, useDriveStore, type NodeDto } from '@/stores/drive'
import { useDriveUploadsStore } from '@/stores/driveUploads'
import { blobUrl } from '@/upload'
import { triggerDownload } from './fileKind'
import { openPreview, type PreviewItem } from './preview'
import NodeItem from './NodeItem.vue'
import MoveDialog from './MoveDialog.vue'
import ShareDialog from './ShareDialog.vue'
import SendToChatDialog from './SendToChatDialog.vue'

const props = defineProps<{ kind: 'my' | 'family' | 'shared'; folderId?: string; spaceId?: string }>()
const { t } = useI18n()
const router = useRouter()
const drive = useDriveStore()
const uploads = useDriveUploadsStore()

const view = useStorage<'grid' | 'list'>('pc.drive.view', 'grid')
const fileInput = ref<HTMLInputElement | null>(null)
const dragging = ref(false)

const moving = ref<NodeDto | null>(null)
const sharing = ref<NodeDto | null>(null)
const sending = ref<NodeDto | null>(null)

/** 当前看的是哪个空间：我的 / 家庭由概览决定，共享来自路由 */
const spaceId = computed(() => {
  if (props.kind === 'shared') return props.spaceId ?? null
  return (props.kind === 'my' ? drive.personal : drive.family)?.id ?? null
})

watch(
  [spaceId, () => props.folderId],
  ([sid, fid]) => {
    if (sid) void drive.open(sid, fid ?? null)
  },
  { immediate: true },
)

const listing = computed(() => (drive.listing && drive.listing.space.id === spaceId.value && (drive.listing.folder?.id ?? undefined) === props.folderId ? drive.listing : null))
/** 首次离线打开网盘、连概览都没有：不能一直转骨架屏 */
const noOverview = computed(() => props.kind !== 'shared' && !spaceId.value && drive.overviewFailed)
const canWrite = computed(() => atLeast(listing.value?.permission, 'Write'))
/** 管理共享 / 跨文件夹移动：需要空间级写权限（被共享的人不行） */
const canManage = computed(() => atLeast(listing.value?.space.permission, 'Write'))

const rootLabel = computed(() => (props.kind === 'my' ? t('drive.my') : props.kind === 'family' ? t('drive.family') : t('drive.shared')))

function folderRoute(id: string | null): string {
  if (props.kind === 'shared') return id ? `/drive/shared/${spaceId.value}/${id}` : '/drive/shared'
  return `/drive/${props.kind}${id ? `/${id}` : ''}`
}

const crumbs = computed(() => listing.value?.path ?? [])

function asPreview(n: NodeDto): PreviewItem {
  return { blobId: n.blobId!, name: n.name, mime: n.mime ?? 'application/octet-stream' }
}

function openNode(n: NodeDto): void {
  if (n.isFolder) {
    void router.push(folderRoute(n.id))
    return
  }
  if (!n.blobId) return
  const siblings = (listing.value?.items ?? []).filter((i) => !i.isFolder && i.blobId).map(asPreview)
  openPreview(asPreview(n), siblings)
}

/** 刷新按钮 / 出错页的"重试"：概览都没拉到时连概览一起拉 */
function reload(): void {
  if (!drive.overview) void drive.loadOverview()
  void drive.refresh()
}

/** 出错页：子目录里回到根目录；已经在根目录（路由不会变）就原地重试 */
function backToRoot(): void {
  if (props.folderId) void router.push(folderRoute(null))
  else reload()
}

// ---------------- 上传 / 新建 ----------------

function pickFiles(): void {
  fileInput.value?.click()
}

function onFiles(evt: Event): void {
  const input = evt.target as HTMLInputElement
  enqueue(Array.from(input.files ?? []))
  input.value = ''
}

function onDrop(evt: DragEvent): void {
  dragging.value = false
  if (!canWrite.value) return
  enqueue(Array.from(evt.dataTransfer?.files ?? []))
}

function enqueue(files: File[]): void {
  const l = listing.value
  if (!l || !files.length) return
  uploads.add(files.filter((f) => f.size > 0), l.space.id, l.folder?.id ?? null)
}

async function newFolder(): Promise<void> {
  const l = listing.value
  if (!l) return
  try {
    const { value } = await ElMessageBox.prompt(t('drive.folderName'), t('drive.newFolder'), {
      inputValue: t('drive.newFolderDefault'),
      confirmButtonText: t('common.confirm'),
      cancelButtonText: t('common.cancel'),
    })
    await drive.createFolder(l.space.id, l.folder?.id ?? null, value)
  } catch (e) {
    if (e instanceof Error) ElMessage.error(e.message)
  }
}

// ---------------- 单项操作 ----------------

async function rename(n: NodeDto): Promise<void> {
  try {
    const { value } = await ElMessageBox.prompt(t('drive.newName'), t('drive.rename'), {
      inputValue: n.name,
      confirmButtonText: t('common.confirm'),
      cancelButtonText: t('common.cancel'),
    })
    if (value.trim() && value.trim() !== n.name) await drive.rename(n, value)
  } catch (e) {
    if (e instanceof Error) ElMessage.error(e.message)
  }
}

async function remove(n: NodeDto): Promise<void> {
  try {
    await ElMessageBox.confirm(t('drive.deleteConfirm', { name: n.name }), t('drive.delete'), {
      type: 'warning',
      confirmButtonText: t('drive.delete'),
      cancelButtonText: t('common.cancel'),
    })
  } catch {
    return
  }
  try {
    await drive.remove(n)
    ElMessage.success(t('drive.deleted'))
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

function onCommand(n: NodeDto, cmd: string): void {
  switch (cmd) {
    case 'open': openNode(n); break
    case 'download': if (n.blobId) triggerDownload(blobUrl(n.blobId, { name: n.name, download: true }), n.name); break
    case 'rename': void rename(n); break
    case 'move': moving.value = n; break
    case 'share': sharing.value = n; break
    case 'send': sending.value = n; break
    case 'delete': void remove(n); break
  }
}
</script>

<template>
  <div
    class="relative flex h-full min-h-0 flex-col"
    data-testid="file-browser"
    @dragover.prevent="canWrite && (dragging = true)"
    @dragleave.prevent="dragging = false"
    @drop.prevent="onDrop"
  >
    <!-- 面包屑 + 工具栏 -->
    <div class="flex flex-wrap items-center gap-2 px-3 py-2" style="border-bottom: 1px solid var(--pc-border)">
      <nav class="flex min-w-0 flex-1 items-center gap-1 overflow-x-auto whitespace-nowrap text-sm" data-testid="drive-crumbs">
        <router-link :to="folderRoute(null)" class="hover:underline" :class="crumbs.length ? '' : 'font-semibold'">{{ rootLabel }}</router-link>
        <template v-for="(c, i) in crumbs" :key="c.id">
          <span style="color: var(--pc-text-muted)">/</span>
          <router-link :to="folderRoute(c.id)" class="hover:underline" :class="i === crumbs.length - 1 ? 'font-semibold' : ''">{{ c.name }}</router-link>
        </template>
      </nav>
      <template v-if="canWrite">
        <el-button type="primary" size="small" :icon="Upload" data-testid="drive-upload" @click="pickFiles">{{ t('drive.upload') }}</el-button>
        <el-button size="small" :icon="FolderAdd" data-testid="drive-new-folder" @click="newFolder">{{ t('drive.newFolder') }}</el-button>
      </template>
      <el-button-group size="small">
        <el-button :icon="Grid" :type="view === 'grid' ? 'primary' : undefined" plain :aria-label="t('drive.grid')" @click="view = 'grid'" />
        <el-button :icon="List" :type="view === 'list' ? 'primary' : undefined" plain :aria-label="t('drive.list')" @click="view = 'list'" />
      </el-button-group>
      <el-button size="small" text :icon="Refresh" :loading="drive.loading" :aria-label="t('drive.refresh')" @click="reload" />
      <input ref="fileInput" type="file" multiple class="hidden" data-testid="drive-file-input" @change="onFiles" />
    </div>

    <div v-if="drive.offlineCopy" class="px-3 py-1 text-xs" style="background: var(--pc-primary-soft); color: var(--pc-primary)">{{ t('drive.offlineCopy') }}</div>

    <div class="min-h-0 flex-1 overflow-y-auto p-3">
      <el-result v-if="noOverview || (drive.error && !listing)" icon="warning" :title="noOverview || drive.error === 'offline' ? t('drive.offlineNoCopy') : drive.error ?? ''">
        <template #extra><el-button @click="backToRoot">{{ folderId ? t('drive.backToRoot') : t('common.retry') }}</el-button></template>
      </el-result>
      <el-skeleton v-else-if="!listing" :rows="4" animated />
      <el-empty v-else-if="!listing.items.length" :description="canWrite ? t('drive.emptyWritable') : t('drive.empty')" />

      <!-- 网格 -->
      <ul v-else-if="view === 'grid'" class="grid gap-3" style="grid-template-columns: repeat(auto-fill, minmax(120px, 1fr))">
        <NodeItem
          v-for="n in listing.items"
          :key="n.id"
          :node="n"
          view="grid"
          :can-write="canWrite"
          :can-manage="canManage"
          @open="openNode(n)"
          @command="(c: string) => onCommand(n, c)"
        />
      </ul>

      <!-- 列表 -->
      <table v-else class="w-full text-sm">
        <tbody>
          <NodeItem
            v-for="n in listing.items"
            :key="n.id"
            :node="n"
            view="list"
            :can-write="canWrite"
            :can-manage="canManage"
            @open="openNode(n)"
            @command="(c: string) => onCommand(n, c)"
          />
        </tbody>
      </table>
    </div>

    <div
      v-if="dragging"
      class="pointer-events-none absolute inset-2 z-10 flex items-center justify-center rounded-xl text-sm"
      style="background: var(--pc-primary-soft); color: var(--pc-primary); border: 2px dashed var(--pc-primary)"
    >
      {{ t('drive.dropHint') }}
    </div>

    <MoveDialog :node="moving" @close="moving = null" />
    <ShareDialog :node="sharing" :space="listing?.space ?? null" @close="sharing = null" />
    <SendToChatDialog :node="sending" @close="sending = null" />
  </div>
</template>
