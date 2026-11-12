<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { api } from '@/api/client'
import { useFamilyStore } from '@/stores/family'
import { useSessionStore } from '@/stores/session'
import { unwrap, type AgentSetupDto } from '@/stores/remote'

const open = defineModel<boolean>({ required: true })
const emit = defineEmits<{ added: [deviceId: string] }>()

const { t } = useI18n()
const family = useFamilyStore()
const session = useSessionStore()

const setup = ref<AgentSetupDto | null>(null)
const code = ref('')
const found = ref<string | null>(null)
const missing = ref(false)
const owner = ref('')
const busy = ref(false)
let lookupSeq = 0

const isAdmin = computed(() => session.user?.role === 'Admin')
const owners = computed(() => family.members.filter((m) => !m.disabled))

watch(open, async (v) => {
  lookupSeq++ // 关掉前发出的查询迟到了也不算数
  if (!v) return
  code.value = ''
  found.value = null
  missing.value = false
  owner.value = session.user?.id ?? ''
  void family.load()
  try {
    setup.value = await unwrap(api.GET('/api/devices/agent-setup'))
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}, { immediate: true })

watch(code, async (raw) => {
  const digits = raw.replace(/\D/g, '').slice(0, 8)
  if (digits !== raw) code.value = digits
  found.value = null
  missing.value = false
  const seq = ++lookupSeq
  if (digits.length !== 8) return
  try {
    const r = await unwrap(api.GET('/api/auth/device/lookup', { params: { query: { code: digits } } }))
    if (seq === lookupSeq) found.value = r.name
  } catch {
    if (seq === lookupSeq) missing.value = true
  }
})

async function copySetup(): Promise<void> {
  if (!setup.value) return
  try {
    await navigator.clipboard.writeText(setup.value.setupCode)
    ElMessage.success(t('remote.copied'))
  } catch {
    // 非安全上下文没有剪贴板接口：选中文本让用户自己复制
    ElMessage.warning(setup.value.setupCode)
  }
}

async function confirm(): Promise<void> {
  if (code.value.length !== 8 || !found.value) return
  busy.value = true
  try {
    const ownerId = isAdmin.value && owner.value && owner.value !== session.user?.id ? owner.value : null
    const r = await unwrap(api.POST('/api/auth/device/confirm', { body: { code: code.value, deviceName: null, ownerId } }))
    ElMessage.success(t('remote.added', { name: found.value }))
    emit('added', r.deviceId)
    open.value = false
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <el-dialog v-model="open" :title="t('remote.addTitle')" width="min(560px, 94vw)" append-to-body data-testid="add-device-dialog">
    <ol class="flex flex-col gap-5">
      <li>
        <div class="mb-2 font-medium">1. {{ t('remote.step1') }}</div>
        <a v-if="setup?.downloadUrl" :href="setup.downloadUrl" download class="inline-block">
          <el-button type="primary" plain>{{ t('remote.download') }}</el-button>
        </a>
        <div v-else-if="setup" class="text-sm" style="color: var(--pc-text-muted)">{{ t('remote.noDownload') }}</div>
      </li>
      <li>
        <div class="mb-2 font-medium">2. {{ t('remote.step2') }}</div>
        <div class="flex gap-2">
          <el-input :model-value="setup?.setupCode ?? ''" readonly data-testid="setup-code" @focus="($event.target as HTMLInputElement).select()" />
          <el-button @click="copySetup">{{ t('remote.copy') }}</el-button>
        </div>
        <div class="mt-1 text-xs" style="color: var(--pc-text-muted)">
          {{ setup?.accessTokenConfigured ? t('remote.setupHint') : t('remote.setupHintNoAccess') }}
        </div>
      </li>
      <li>
        <div class="mb-2 font-medium">3. {{ t('remote.step3') }}</div>
        <el-input v-model="code" :placeholder="t('remote.codePlaceholder')" inputmode="numeric" maxlength="8" size="large" class="!w-48 font-mono" data-testid="device-code" />
        <div v-if="found" class="mt-2 text-sm" style="color: #30a46c" data-testid="device-found">{{ t('remote.lookupFound', { name: found }) }}</div>
        <div v-else-if="missing" class="mt-2 text-sm" style="color: #e5484d">{{ t('remote.lookupMissing') }}</div>
        <div v-if="isAdmin && found" class="mt-3 flex items-center gap-2">
          <span class="text-sm">{{ t('remote.ownerLabel') }}</span>
          <el-select v-model="owner" class="!w-48" data-testid="device-owner">
            <el-option v-for="m in owners" :key="m.id" :label="m.displayName || m.username" :value="m.id" />
          </el-select>
        </div>
      </li>
    </ol>
    <template #footer>
      <el-button @click="open = false">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="busy" :disabled="!found" data-testid="confirm-device" @click="confirm">{{ t('remote.confirmAdd') }}</el-button>
    </template>
  </el-dialog>
</template>
