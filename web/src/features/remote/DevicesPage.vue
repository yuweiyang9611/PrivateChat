<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Monitor, MoreFilled, Plus, View, Mouse } from '@element-plus/icons-vue'
import { api } from '@/api/client'
import { useFamilyStore } from '@/stores/family'
import { useSessionStore } from '@/stores/session'
import { unwrap, useRemoteStore, type DeviceDto, type RemoteSessionDto, type RemoteSessionMode } from '@/stores/remote'
import { online } from '@/net/online'
import AddDeviceDialog from './AddDeviceDialog.vue'
import DeviceSettingsDialog from './DeviceSettingsDialog.vue'
import { formatBytes, formatWhen, sessionMinutes } from './format'

const { t } = useI18n()
const router = useRouter()
const remote = useRemoteStore()
const family = useFamilyStore()
const session = useSessionStore()

const adding = ref(false)
const editing = ref<DeviceDto | null>(null)
const editOpen = ref(false)
const starting = ref<string | null>(null)
const history = ref<RemoteSessionDto[]>([])
const historyNext = ref<string | null>(null)
const loadingHistory = ref(false)
const loadError = ref('')

const me = computed(() => session.user?.id)
const isChild = computed(() => session.user?.role === 'Child')

onMounted(async () => {
  void family.load()
  try {
    await remote.load()
  } catch (e) {
    loadError.value = (e as Error).message
  }
  if (!isChild.value) void loadHistory()
})

async function loadHistory(more = false): Promise<void> {
  loadingHistory.value = true
  try {
    const page = await unwrap(api.GET('/api/remote/sessions', { params: { query: { before: more ? historyNext.value ?? undefined : undefined, limit: 20 } } }))
    history.value = more ? [...history.value, ...page.items] : page.items
    historyNext.value = page.nextBefore
  } catch {
    // 记录拉不到不影响主要功能
  } finally {
    loadingHistory.value = false
  }
}

function ownerText(d: DeviceDto): string {
  return d.ownerId === me.value ? t('remote.mine') : t('remote.ownerOf', { name: family.nameOf(d.ownerId) })
}

function screensText(d: DeviceDto): string {
  if (!d.screens.length) return ''
  if (d.screens.length > 1) return t('remote.screensMany', { n: d.screens.length })
  return `${d.screens[0]!.width}×${d.screens[0]!.height}`
}

function sessionText(s: RemoteSessionDto): string {
  const who = s.controllerUserId === me.value ? '我' : family.nameOf(s.controllerUserId)
  if (s.state === 'Pending') return t('remote.waitingConsent', { who })
  return t('remote.inUse', { who, mode: s.mode === 'View' ? t('remote.modeView') : t('remote.modeControl') })
}

function canEnd(d: DeviceDto): boolean {
  return !!d.session && (d.canManage || d.session.controllerUserId === me.value)
}

async function start(d: DeviceDto, mode: RemoteSessionMode, pin?: string): Promise<void> {
  if (!online.value) return
  starting.value = d.id
  try {
    const s = await remote.start(d.id, mode, pin)
    await router.push(`/remote/${s.id}`)
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    starting.value = null
  }
}

async function startWithPin(d: DeviceDto): Promise<void> {
  try {
    const { value } = await ElMessageBox.prompt(t('remote.pinPrompt'), t('remote.pinTitle'), {
      inputType: 'password',
      inputPattern: /^\d{6,12}$/,
      inputErrorMessage: '6 到 12 位数字',
      confirmButtonText: t('remote.control'),
      cancelButtonText: t('common.cancel'),
    })
    await start(d, 'Control', value)
  } catch {
    // 取消
  }
}

async function endSession(d: DeviceDto): Promise<void> {
  if (!d.session) return
  try {
    await remote.end(d.session.id)
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

async function revoke(d: DeviceDto): Promise<void> {
  try {
    await ElMessageBox.confirm(t('remote.revokeConfirm', { name: d.name }), t('remote.revoke'), { type: 'warning', confirmButtonText: t('remote.revoke'), cancelButtonText: t('common.cancel') })
  } catch {
    return
  }
  try {
    await remote.revoke(d.id)
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

function onCommand(d: DeviceDto, command: string): void {
  if (command === 'pin') void startWithPin(d)
  else if (command === 'settings') {
    editing.value = d
    editOpen.value = true
  } else if (command === 'revoke') void revoke(d)
}

function historyText(s: RemoteSessionDto): string {
  const who = s.controllerUserId === me.value ? '我' : family.nameOf(s.controllerUserId)
  return t('remote.historyRow', { who, mode: s.mode === 'View' ? t('remote.modeView') : t('remote.modeControl'), device: s.deviceName })
}

function historyDetail(s: RemoteSessionDto): string {
  const parts = [formatWhen(s.startedAt)]
  const minutes = sessionMinutes(s)
  if (minutes !== null) parts.push(minutes === 0 ? t('remote.underMinute') : t('remote.minutes', { n: minutes }))
  if (s.bytesRelayed > 0) parts.push(formatBytes(s.bytesRelayed))
  if (s.endReason && s.endReason in reasonKeys) parts.push(t(`remote.reasons.${s.endReason}`))
  return parts.join(' · ')
}

const reasonKeys = { declined: 1, timeout: 1, controller: 1, agent: 1, stopped: 1, revoked: 1, disconnected: 1, replaced: 1 }
</script>

<template>
  <div class="h-full overflow-y-auto" data-testid="devices-page">
    <div class="mx-auto flex max-w-2xl flex-col gap-4 p-4">
      <div class="flex items-center justify-between">
        <h1 class="text-lg font-semibold">{{ t('settings.devices') }}</h1>
        <el-button v-if="!isChild" type="primary" round :icon="Plus" :disabled="!online" data-testid="add-device" @click="adding = true">{{ t('remote.add') }}</el-button>
      </div>

      <el-alert v-if="loadError" type="error" :closable="false" :title="loadError" />
      <el-empty v-else-if="remote.loaded && !remote.devices.length" :description="t('remote.empty')" :image-size="72" />

      <ul v-else class="flex flex-col gap-3">
        <li v-for="d in remote.devices" :key="d.id" class="rounded-2xl border p-4" style="border-color: var(--pc-border); background: var(--pc-surface)" data-testid="device" :data-device-id="d.id">
          <div class="flex items-center gap-3">
            <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-2xl" :style="d.online ? 'background: var(--pc-primary-soft); color: var(--pc-primary)' : 'background: var(--pc-bg); color: var(--pc-text-muted)'">
              <el-icon :size="26"><Monitor /></el-icon>
            </div>
            <div class="min-w-0 flex-1">
              <div class="flex items-center gap-2">
                <span class="truncate font-semibold" data-testid="device-title">{{ d.name }}</span>
                <el-tag :type="d.online ? 'success' : 'info'" size="small" round data-testid="device-online">{{ d.online ? t('remote.online') : t('remote.offline') }}</el-tag>
              </div>
              <div class="truncate text-sm" style="color: var(--pc-text-muted)">
                {{ ownerText(d) }}
                <template v-if="d.online && screensText(d)"> · {{ screensText(d) }}</template>
                <template v-else-if="!d.online && d.lastSeenAt"> · {{ t('remote.lastSeen', { time: formatWhen(d.lastSeenAt) }) }}</template>
              </div>
            </div>
            <el-dropdown v-if="d.canManage || (d.hasPin && d.canControl)" trigger="click" @command="(c: string) => onCommand(d, c)">
              <el-button :icon="MoreFilled" circle text data-testid="device-more" />
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item v-if="d.hasPin && d.canControl && d.online" command="pin">{{ t('remote.usePin') }}</el-dropdown-item>
                  <el-dropdown-item v-if="d.canManage" command="settings" data-testid="device-settings">{{ t('remote.settings') }}</el-dropdown-item>
                  <el-dropdown-item v-if="d.canManage" command="revoke" divided data-testid="device-revoke">{{ t('remote.revoke') }}</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </div>

          <div v-if="d.session" class="mt-3 flex items-center gap-2 rounded-xl px-3 py-2 text-sm" style="background: var(--pc-primary-soft)" data-testid="device-session">
            <span class="min-w-0 flex-1 truncate">{{ sessionText(d.session) }}</span>
            <el-button v-if="d.session.controllerUserId === me" size="small" round type="primary" @click="router.push(`/remote/${d.session.id}`)">{{ t('remote.backToSession') }}</el-button>
            <el-button v-if="canEnd(d)" size="small" round type="danger" plain data-testid="device-end-session" @click="endSession(d)">{{ t('remote.endSession') }}</el-button>
          </div>

          <div v-if="d.canControl" class="mt-3 flex gap-2">
            <el-button round :icon="View" :disabled="!d.online || !online || starting === d.id" data-testid="device-view" @click="start(d, 'View')">{{ t('remote.view') }}</el-button>
            <el-button round type="primary" :icon="Mouse" :disabled="!d.online || !online || starting === d.id" :loading="starting === d.id" data-testid="device-control" @click="start(d, 'Control')">{{ t('remote.control') }}</el-button>
          </div>
          <div v-else class="mt-2 text-xs" style="color: var(--pc-text-muted)">{{ t('remote.noPermission') }}</div>
        </li>
      </ul>

      <section v-if="!isChild">
        <h2 class="mb-2 text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('remote.history') }}</h2>
        <div v-if="!loadingHistory && !history.length" class="py-4 text-center text-sm" style="color: var(--pc-text-muted)">{{ t('remote.historyEmpty') }}</div>
        <ul v-else class="overflow-hidden rounded-2xl border" style="border-color: var(--pc-border); background: var(--pc-surface)" data-testid="remote-history">
          <li v-for="s in history" :key="s.id" class="px-4 py-2.5" data-testid="remote-history-item">
            <div class="truncate">{{ historyText(s) }}</div>
            <div class="truncate text-xs" style="color: var(--pc-text-muted)">{{ historyDetail(s) }}</div>
          </li>
        </ul>
        <div v-if="historyNext" class="mt-2 text-center">
          <el-button text :loading="loadingHistory" @click="loadHistory(true)">{{ t('moments.loadMore') }}</el-button>
        </div>
      </section>
    </div>

    <AddDeviceDialog v-model="adding" @added="() => remote.load()" />
    <DeviceSettingsDialog v-model="editOpen" :device="editing" />
  </div>
</template>
