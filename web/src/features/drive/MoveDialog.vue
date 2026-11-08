<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { atLeast, useDriveStore, type FolderRefDto, type NodeDto } from '@/stores/drive'

const props = defineProps<{ node: NodeDto | null }>()
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()
const drive = useDriveStore()

const open = computed({ get: () => props.node !== null, set: (v) => { if (!v) emit('close') } })
/** 只能移到空间级可写的空间（自己的 / 家庭） */
const spaces = computed(() => (drive.overview?.spaces ?? []).filter((s) => atLeast(s.permission, 'Write')))
const spaceId = ref<string>('')
const folders = ref<FolderRefDto[]>([])
const target = ref<string | null>(null)
const busy = ref(false)
const loadingTree = ref(false)

interface TreeNode { id: string; label: string; children: TreeNode[] }

/** 被移动的文件夹及其子孙不能当目标 */
const excluded = computed(() => {
  const n = props.node
  const out = new Set<string>()
  if (!n?.isFolder) return out
  out.add(n.id)
  let grew = true
  while (grew) {
    grew = false
    for (const f of folders.value) if (f.parentId && out.has(f.parentId) && !out.has(f.id)) { out.add(f.id); grew = true }
  }
  return out
})

const tree = computed<TreeNode[]>(() => {
  const build = (parent: string | null): TreeNode[] =>
    folders.value
      .filter((f) => (f.parentId ?? null) === parent && !excluded.value.has(f.id))
      .sort((a, b) => a.name.localeCompare(b.name, 'zh-CN'))
      .map((f) => ({ id: f.id, label: f.name, children: build(f.id) }))
  return [{ id: '__root__', label: t('drive.rootFolder'), children: build(null) }]
})

async function loadFolders(): Promise<void> {
  if (!spaceId.value) return
  loadingTree.value = true
  try {
    folders.value = await drive.folders(spaceId.value)
  } catch (e) {
    folders.value = []
    ElMessage.error((e as Error).message)
  } finally {
    loadingTree.value = false
  }
}

watch(
  () => props.node,
  async (n) => {
    if (!n) return
    if (!drive.overview) await drive.loadOverview()
    spaceId.value = spaces.value.find((s) => s.id === n.spaceId)?.id ?? spaces.value[0]?.id ?? ''
    target.value = null
    await loadFolders()
  },
  { immediate: true },
)
watch(spaceId, () => { target.value = null; void loadFolders() })

function onPick(data: TreeNode): void {
  target.value = data.id === '__root__' ? null : data.id
}

async function confirm(): Promise<void> {
  const n = props.node
  if (!n || !spaceId.value) return
  busy.value = true
  try {
    await drive.move(n, { spaceId: spaceId.value, parentId: target.value })
    ElMessage.success(t('drive.moved'))
    emit('close')
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <el-dialog v-model="open" :title="t('drive.moveTitle', { name: node?.name ?? '' })" width="min(92vw, 460px)" append-to-body data-testid="move-dialog">
    <el-radio-group v-if="spaces.length > 1" v-model="spaceId" class="mb-3">
      <el-radio-button v-for="s in spaces" :key="s.id" :value="s.id">{{ s.name }}</el-radio-button>
    </el-radio-group>
    <div v-loading="loadingTree" class="max-h-[50vh] overflow-y-auto rounded border p-1" style="border-color: var(--pc-border)">
      <el-tree :data="tree" node-key="id" :props="{ label: 'label', children: 'children' }" default-expand-all highlight-current :expand-on-click-node="false" current-node-key="__root__" @node-click="onPick" />
    </div>
    <template #footer>
      <el-button @click="emit('close')">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="busy" data-testid="move-confirm" @click="confirm">{{ t('drive.moveHere') }}</el-button>
    </template>
  </el-dialog>
</template>
