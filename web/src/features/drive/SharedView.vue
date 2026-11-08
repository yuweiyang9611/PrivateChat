<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { useDriveStore, type SharedNodeDto } from '@/stores/drive'
import { formatBytes, KIND_EMOJI, kindOfMime } from './fileKind'
import { openPreview } from './preview'

const { t } = useI18n()
const router = useRouter()
const drive = useDriveStore()
const items = ref<SharedNodeDto[]>([])
const loading = ref(false)
const failed = ref(false)

onMounted(async () => {
  loading.value = true
  try {
    items.value = await drive.sharedWithMe()
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
})

function open(s: SharedNodeDto): void {
  const n = s.node
  if (n.isFolder) void router.push(`/drive/shared/${n.spaceId}/${n.id}`)
  else if (n.blobId) openPreview({ blobId: n.blobId, name: n.name, mime: n.mime ?? 'application/octet-stream' })
}
</script>

<template>
  <div v-loading="loading" class="h-full overflow-y-auto p-3" data-testid="shared-with-me">
    <el-empty v-if="!loading && !items.length" :description="failed ? t('drive.offlineNoCopy') : t('drive.sharedEmpty')" />
    <ul>
      <li v-for="s in items" :key="s.node.id">
        <button type="button" class="flex w-full items-center gap-3 py-2 text-left" style="border-bottom: 1px solid var(--pc-border)" :data-node-name="s.node.name" @click="open(s)">
          <span class="text-2xl">{{ KIND_EMOJI[kindOfMime(s.node.mime, s.node.isFolder)] }}</span>
          <div class="min-w-0 flex-1">
            <div class="truncate">{{ s.node.name }}</div>
            <div class="text-xs" style="color: var(--pc-text-muted)">
              {{ t('drive.sharedBy', { name: s.ownerName }) }} · {{ s.permission === 'Write' ? t('drive.permWrite') : t('drive.permRead') }}
              <template v-if="!s.node.isFolder"> · {{ formatBytes(s.node.size) }}</template>
            </div>
          </div>
        </button>
      </li>
    </ul>
  </div>
</template>
