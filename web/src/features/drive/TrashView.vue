<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useDriveStore, type TrashItemDto } from '@/stores/drive'
import { formatBytes, KIND_EMOJI, kindOfMime } from './fileKind'

const { t } = useI18n()
const drive = useDriveStore()
const items = ref<TrashItemDto[]>([])
const loading = ref(false)
const RETENTION_DAYS = 30

async function load(): Promise<void> {
  loading.value = true
  try {
    items.value = await drive.trash()
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    loading.value = false
  }
}
onMounted(load)

function daysLeft(i: TrashItemDto): number {
  const ms = new Date(i.deletedAt).getTime() + RETENTION_DAYS * 86_400_000 - Date.now()
  return Math.max(0, Math.ceil(ms / 86_400_000))
}

async function restore(i: TrashItemDto): Promise<void> {
  try {
    const node = await drive.restore(i.node.id)
    ElMessage.success(t('drive.restored', { name: node.name }))
    await load()
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

async function purge(i: TrashItemDto): Promise<void> {
  try {
    await ElMessageBox.confirm(t('drive.purgeConfirm', { name: i.node.name }), t('drive.purge'), { type: 'warning', confirmButtonText: t('drive.purge'), cancelButtonText: t('common.cancel') })
  } catch {
    return
  }
  try {
    await drive.purge(i.node.id)
    await load()
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

async function emptyAll(): Promise<void> {
  try {
    await ElMessageBox.confirm(t('drive.emptyConfirm'), t('drive.emptyTrash'), { type: 'warning', confirmButtonText: t('drive.emptyTrash'), cancelButtonText: t('common.cancel') })
  } catch {
    return
  }
  try {
    await drive.emptyTrash()
    await load()
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}
</script>

<template>
  <div class="flex h-full min-h-0 flex-col" data-testid="trash">
    <div class="flex items-center gap-2 px-3 py-2" style="border-bottom: 1px solid var(--pc-border)">
      <span class="flex-1 text-sm" style="color: var(--pc-text-muted)">{{ t('drive.trashHint', { n: RETENTION_DAYS }) }}</span>
      <el-button size="small" type="danger" plain :disabled="!items.length" @click="emptyAll">{{ t('drive.emptyTrash') }}</el-button>
    </div>
    <div v-loading="loading" class="min-h-0 flex-1 overflow-y-auto p-3">
      <el-empty v-if="!loading && !items.length" :description="t('drive.trashEmpty')" />
      <ul v-else>
        <li v-for="i in items" :key="i.node.id" class="flex items-center gap-3 py-2" style="border-bottom: 1px solid var(--pc-border)" :data-node-name="i.node.name">
          <span class="text-2xl">{{ KIND_EMOJI[kindOfMime(i.node.mime, i.node.isFolder)] }}</span>
          <div class="min-w-0 flex-1">
            <div class="truncate">{{ i.node.name }}</div>
            <div class="text-xs" style="color: var(--pc-text-muted)">
              {{ i.spaceName }} · {{ i.node.isFolder ? t('drive.itemCount', { n: i.itemCount }) : formatBytes(i.totalBytes) }} · {{ t('drive.daysLeft', { n: daysLeft(i) }) }}
            </div>
          </div>
          <el-button size="small" data-testid="trash-restore" @click="restore(i)">{{ t('drive.restore') }}</el-button>
          <el-button size="small" text type="danger" @click="purge(i)">{{ t('drive.purge') }}</el-button>
        </li>
      </ul>
    </div>
  </div>
</template>
