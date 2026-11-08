<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { CopyDocument, Delete } from '@element-plus/icons-vue'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { useDriveStore, type NodeDto, type ShareDto, type SharePermission, type SpaceDto } from '@/stores/drive'
import { useSessionStore } from '@/stores/session'

type MemberDto = components['schemas']['MemberDto']

const props = defineProps<{ node: NodeDto | null; space: SpaceDto | null }>()
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()
const drive = useDriveStore()
const session = useSessionStore()

const open = computed({ get: () => props.node !== null, set: (v) => { if (!v) emit('close') } })
const shares = ref<ShareDto[]>([])
const members = ref<MemberDto[]>([])
const loading = ref(false)

const grants = computed(() => shares.value.filter((s) => !s.isLink))
const links = computed(() => shares.value.filter((s) => s.isLink))
const candidates = computed(() =>
  members.value.filter((m) => m.id !== session.user?.id && !m.disabled && !grants.value.some((g) => g.granteeUserId === m.id)),
)

const newGrantee = ref<string>('')
const newPermission = ref<SharePermission>('Read')
const linkPassword = ref('')
/** 0 表示永久有效（el-option 的 value 不能是 null） */
const linkDays = ref<number>(7)
const busy = ref(false)

async function reload(): Promise<void> {
  const n = props.node
  if (!n) return
  loading.value = true
  try {
    const [s, m] = await Promise.all([drive.shares(n.id), api.GET('/api/family/members')])
    shares.value = s
    members.value = m.data ?? []
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    loading.value = false
  }
}

watch(() => props.node, (n) => {
  if (!n) return
  newGrantee.value = ''
  newPermission.value = 'Read'
  linkPassword.value = ''
  linkDays.value = 7
  void reload()
}, { immediate: true })

async function run(fn: () => Promise<unknown>): Promise<void> {
  busy.value = true
  try {
    await fn()
    await reload()
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    busy.value = false
  }
}

const addGrant = () => run(async () => {
  if (!props.node || !newGrantee.value) return
  await drive.share(props.node.id, { granteeUserId: newGrantee.value, permission: newPermission.value })
  newGrantee.value = ''
})
const changePermission = (g: ShareDto, permission: SharePermission) =>
  run(() => drive.share(props.node!.id, { granteeUserId: g.granteeUserId, permission }))
const revoke = (s: ShareDto) => run(() => drive.revoke(s.id))
const createLink = () => run(async () => {
  if (!props.node) return
  const s = await drive.share(props.node.id, { link: true, password: linkPassword.value || null, expiresInDays: linkDays.value || null })
  linkPassword.value = ''
  if (s.url) await copy(s.url)
})

async function copy(url: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(url)
    ElMessage.success(t('drive.linkCopied'))
  } catch {
    ElMessage.info(url)
  }
}

function expiry(s: ShareDto): string {
  return s.expiresAt ? t('drive.expiresAt', { at: new Date(s.expiresAt).toLocaleDateString('zh-CN') }) : t('drive.noExpiry')
}
</script>

<template>
  <el-dialog v-model="open" :title="t('drive.shareTitle', { name: node?.name ?? '' })" width="min(94vw, 520px)" append-to-body data-testid="share-dialog">
    <div v-loading="loading">
      <p v-if="space?.type === 'Family'" class="mb-3 text-xs" style="color: var(--pc-text-muted)">{{ t('drive.familyShareHint') }}</p>

      <h3 class="mb-2 text-sm font-semibold">{{ t('drive.shareMembers') }}</h3>
      <div v-for="g in grants" :key="g.id" class="mb-2 flex items-center gap-2">
        <span class="flex-1 truncate">{{ g.granteeName }}</span>
        <el-select :model-value="g.permission" size="small" style="width: 110px" @change="(v: SharePermission) => changePermission(g, v)">
          <el-option value="Read" :label="t('drive.permRead')" />
          <el-option value="Write" :label="t('drive.permWrite')" />
        </el-select>
        <el-button size="small" text :icon="Delete" :aria-label="t('drive.revoke')" @click="revoke(g)" />
      </div>
      <div class="mb-4 flex items-center gap-2">
        <el-select v-model="newGrantee" size="small" :placeholder="t('drive.pickMember')" class="flex-1" data-testid="share-member">
          <el-option v-for="m in candidates" :key="m.id" :value="m.id" :label="m.displayName" />
        </el-select>
        <el-select v-model="newPermission" size="small" style="width: 110px">
          <el-option value="Read" :label="t('drive.permRead')" />
          <el-option value="Write" :label="t('drive.permWrite')" />
        </el-select>
        <el-button size="small" type="primary" :disabled="!newGrantee" :loading="busy" data-testid="share-add" @click="addGrant">{{ t('drive.shareAdd') }}</el-button>
      </div>

      <h3 class="mb-2 text-sm font-semibold">{{ t('drive.shareLinks') }}</h3>
      <div v-for="l in links" :key="l.id" class="mb-2 rounded border p-2 text-sm" style="border-color: var(--pc-border)">
        <div class="flex items-center gap-2">
          <span class="flex-1 truncate font-mono text-xs" :title="l.url ?? ''" data-testid="share-link-url">{{ l.url }}</span>
          <el-button size="small" text :icon="CopyDocument" @click="copy(l.url!)" />
          <el-button size="small" text :icon="Delete" @click="revoke(l)" />
        </div>
        <div class="text-xs" style="color: var(--pc-text-muted)">
          {{ l.hasPassword ? t('drive.withPassword') : t('drive.noPassword') }} · {{ expiry(l) }}
        </div>
      </div>
      <p v-if="session.isChild" class="text-xs" style="color: var(--pc-text-muted)">{{ t('drive.childNoLink') }}</p>
      <div v-else class="flex flex-wrap items-center gap-2">
        <el-input v-model="linkPassword" size="small" :placeholder="t('drive.linkPassword')" style="width: 150px" show-password />
        <el-select v-model="linkDays" size="small" style="width: 110px">
          <el-option :value="1" :label="t('drive.days', { n: 1 })" />
          <el-option :value="7" :label="t('drive.days', { n: 7 })" />
          <el-option :value="30" :label="t('drive.days', { n: 30 })" />
          <el-option :value="365" :label="t('drive.days', { n: 365 })" />
          <el-option :value="0" :label="t('drive.noExpiry')" />
        </el-select>
        <el-button size="small" :loading="busy" data-testid="share-create-link" @click="createLink">{{ t('drive.createLink') }}</el-button>
      </div>
      <p class="mt-2 text-xs" style="color: var(--pc-text-muted)">{{ t('drive.linkHint') }}</p>
    </div>
  </el-dialog>
</template>
