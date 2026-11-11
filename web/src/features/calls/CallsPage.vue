<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { House, Phone, VideoCamera } from '@element-plus/icons-vue'
import { callsApi, type CallDto, type CallType, type CallUsageDto } from './api'
import { callDuration, callOutcome, outcomeText } from './format'
import { callsSupported } from './rtc/media'
import { useCallsStore } from '@/stores/calls'
import { useConversationsStore } from '@/stores/conversations'
import { useFamilyStore } from '@/stores/family'
import { useSessionStore } from '@/stores/session'

// 通话页：客厅（常驻会议室）、进行中的通话、给家人打电话、最近通话。
const { t } = useI18n()
const calls = useCallsStore()
const conversations = useConversationsStore()
const family = useFamilyStore()
const session = useSessionStore()

const supported = callsSupported()
const history = ref<CallDto[]>([])
const usage = ref<CallUsageDto | null>(null)
const loadingHistory = ref(true)

const me = computed(() => session.user?.id)
const room = computed(() => calls.rooms[0] ?? null)
const inRoom = computed(() => !!room.value && calls.current?.callId === room.value.id)
const roomPeople = computed(() => (room.value?.participants ?? []).map((p) => family.nameOf(p.userId)))
/** 我还没进去的进行中通话 */
const joinable = computed(() => calls.ongoing.filter((c) => c.id !== calls.current?.callId))
const relatives = computed(() => family.members.filter((m) => m.id !== me.value && !m.disabled))
const disabled = computed(() => !supported || !calls.enabled || calls.entering)

async function loadHistory(): Promise<void> {
  try {
    history.value = (await callsApi.history()).filter((c) => c.endedAt)
  } catch {
    // 离线：保留已有的
  } finally {
    loadingHistory.value = false
  }
}

onMounted(() => {
  void family.load()
  void calls.refresh()
  void loadHistory()
  if (session.isAdmin) void callsApi.usage().then((u) => { usage.value = u }, () => undefined)
})
// 一通电话打完：记录里多一条
watch(() => calls.current, (v, old) => { if (!v && old) setTimeout(() => void loadHistory(), 500) })
watch(() => calls.ongoing.length, () => void loadHistory())

function titleOf(c: CallDto): string {
  return family.titleOf(conversations.byId(c.conversationId ?? '')) || family.nameOf(c.startedBy)
}

function summary(c: CallDto): string {
  const text = outcomeText(callOutcome(c.endReason ?? 'Completed', c.startedBy, me.value), callDuration(c))
  return t(text.key, text.params ?? {})
}

function missed(c: CallDto): boolean {
  return callOutcome(c.endReason ?? 'Completed', c.startedBy, me.value) === 'missed-by-me'
}

function when(iso: string): string {
  const d = new Date(iso)
  const sameDay = d.toDateString() === new Date().toDateString()
  return sameDay
    ? d.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
    : d.toLocaleDateString('zh-CN', { month: 'numeric', day: 'numeric' }) + ' ' + d.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
}

function callBack(c: CallDto): void {
  if (c.conversationId) void calls.startCall(c.conversationId, c.type === 'Audio' ? 'Audio' : 'Video')
}

function dial(userId: string, type: CallType): void {
  void calls.callMember(userId, type)
}

function gigabytes(bytes: number): string {
  return (bytes / 1024 ** 3).toFixed(bytes > 1024 ** 3 ? 1 : 2)
}
</script>

<template>
  <div class="h-full overflow-y-auto" data-testid="calls-page">
    <div class="mx-auto flex max-w-2xl flex-col gap-4 p-4">
      <el-alert v-if="!supported" type="warning" :closable="false" show-icon :title="t('calls.unsupported')" data-testid="calls-unsupported" />
      <el-alert v-else-if="calls.config && !calls.enabled" type="warning" :closable="false" show-icon :title="t('calls.notConfigured')" data-testid="calls-disabled" />

      <!-- 客厅：常驻会议室，随时可进 -->
      <section v-if="room" class="flex items-center gap-4 rounded-2xl border p-4" style="border-color: var(--pc-border); background: var(--pc-surface)" data-testid="living-room">
        <div class="flex h-14 w-14 shrink-0 items-center justify-center rounded-2xl" style="background: var(--pc-primary-soft); color: var(--pc-primary)">
          <el-icon :size="30"><House /></el-icon>
        </div>
        <div class="min-w-0 flex-1">
          <div class="text-lg font-semibold">{{ t('calls.livingRoom') }}</div>
          <div class="truncate text-sm" style="color: var(--pc-text-muted)" data-testid="living-room-people">
            {{ roomPeople.length ? t('calls.roomPeople', { names: roomPeople.join('、') }) : t('calls.roomEmpty') }}
          </div>
        </div>
        <el-button v-if="inRoom" type="primary" plain round @click="calls.setMinimized(false)">{{ t('calls.backToCall') }}</el-button>
        <el-button v-else type="primary" round :disabled="disabled" data-testid="living-room-join" @click="calls.join(room.id)">{{ t('calls.enterRoom') }}</el-button>
      </section>

      <!-- 进行中的通话 -->
      <section v-if="joinable.length">
        <h2 class="mb-2 text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('calls.ongoing') }}</h2>
        <ul class="overflow-hidden rounded-2xl border" style="border-color: var(--pc-border); background: var(--pc-surface)">
          <li v-for="c in joinable" :key="c.id" class="flex items-center gap-3 px-4 py-3" data-testid="ongoing-call">
            <el-icon :size="22" style="color: #30a46c"><component :is="c.type === 'Audio' ? Phone : VideoCamera" /></el-icon>
            <div class="min-w-0 flex-1">
              <div class="truncate font-medium">{{ titleOf(c) }}</div>
              <div class="truncate text-sm" style="color: var(--pc-text-muted)">
                {{ c.participants.map((p) => family.nameOf(p.userId)).join('、') || t('calls.ringing') }}
              </div>
            </div>
            <el-button type="success" round :disabled="disabled" @click="calls.join(c.id)">{{ t('calls.joinCall') }}</el-button>
          </li>
        </ul>
      </section>

      <!-- 家人：一键拨打 -->
      <section>
        <h2 class="mb-2 text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('calls.family') }}</h2>
        <el-empty v-if="family.loaded && !relatives.length" :description="t('calls.noFamily')" :image-size="64" />
        <ul v-else class="overflow-hidden rounded-2xl border" style="border-color: var(--pc-border); background: var(--pc-surface)">
          <li v-for="m in relatives" :key="m.id" class="flex items-center gap-3 px-4 py-2.5" data-testid="family-member" :data-user-id="m.id">
            <el-avatar :size="44" style="background: var(--pc-primary)">{{ (m.displayName || m.username).slice(0, 1) }}</el-avatar>
            <div class="min-w-0 flex-1 truncate font-medium">{{ m.displayName || m.username }}</div>
            <el-button :icon="Phone" circle size="large" :disabled="disabled" :title="t('calls.audioCall')" data-testid="dial-audio" @click="dial(m.id, 'Audio')" />
            <el-button :icon="VideoCamera" circle size="large" type="primary" :disabled="disabled" :title="t('calls.videoCall')" data-testid="dial-video" @click="dial(m.id, 'Video')" />
          </li>
        </ul>
      </section>

      <!-- 最近通话 -->
      <section>
        <h2 class="mb-2 text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('calls.recent') }}</h2>
        <div v-if="!loadingHistory && !history.length" class="py-6 text-center text-sm" style="color: var(--pc-text-muted)">{{ t('calls.noRecent') }}</div>
        <ul v-else-if="history.length" class="overflow-hidden rounded-2xl border" style="border-color: var(--pc-border); background: var(--pc-surface)" data-testid="call-history">
          <li v-for="c in history" :key="c.id" class="flex items-center gap-3 px-4 py-2.5" data-testid="history-item">
            <el-icon :size="20" :style="missed(c) ? 'color: #e5484d' : 'color: var(--pc-text-muted)'"><component :is="c.type === 'Audio' ? Phone : VideoCamera" /></el-icon>
            <div class="min-w-0 flex-1">
              <div class="truncate font-medium" :style="missed(c) ? 'color: #e5484d' : ''">{{ titleOf(c) }}</div>
              <div class="truncate text-sm" style="color: var(--pc-text-muted)">{{ summary(c) }} · {{ when(c.startedAt) }}</div>
            </div>
            <el-button text round :disabled="disabled" @click="callBack(c)">{{ t('calls.callBack') }}</el-button>
          </li>
        </ul>
      </section>

      <p v-if="usage" class="text-center text-xs" style="color: var(--pc-text-muted)" data-testid="call-usage">
        {{ t('calls.usage', { n: usage.calls, minutes: Math.round(usage.seconds / 60), gb: gigabytes(usage.egressBytesEstimate) }) }}
      </p>
    </div>
  </div>
</template>
