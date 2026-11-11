<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useWindowSize } from '@vueuse/core'
import { Microphone, Minus, Monitor, Mute, PhoneFilled, Refresh, VideoCamera, FullScreen } from '@element-plus/icons-vue'
import ParticipantTile from './ParticipantTile.vue'
import CallAudio from './CallAudio.vue'
import { hasMultipleCameras, screenShareSupported } from './rtc/media'
import { formatDuration } from './format'
import { useCallsStore } from '@/stores/calls'
import { useConversationsStore } from '@/stores/conversations'
import { useFamilyStore } from '@/stores/family'
import { useSessionStore } from '@/stores/session'

// 通话界面（设计稿 5.5）：全屏，或缩成悬浮条继续看聊天。只在 calls.current 存在时挂载。
const { t } = useI18n()
const calls = useCallsStore()
const conversations = useConversationsStore()
const family = useFamilyStore()
const session = useSessionStore()
// 每秒走一次的时钟，给通话计时用
const now = ref(Date.now())
const ticker = setInterval(() => { now.value = Date.now() }, 1000)
onBeforeUnmount(() => clearInterval(ticker))
const { width } = useWindowSize()

const cur = computed(() => calls.current!)
const call = computed(() => calls.currentCall)
const canShare = screenShareSupported()
const canFlip = ref(false)
onMounted(async () => { canFlip.value = await hasMultipleCameras() })

const title = computed(() => {
  const c = call.value
  if (!c) return ''
  if (c.roomSlug) return t('calls.livingRoom')
  return family.titleOf(conversations.byId(c.conversationId ?? '')) || t('nav.calls')
})

/** 计时起点：通话是接通的时刻；会议室是我进来的时刻 */
const since = computed(() => {
  const c = call.value
  if (!c) return null
  if (!c.roomSlug) return c.answeredAt
  return c.participants.find((p) => p.id === cur.value.participantId)?.joinedAt ?? null
})
const timer = computed(() => (since.value ? formatDuration((now.value - new Date(since.value).getTime()) / 1000) : ''))

const status = computed(() => {
  const c = cur.value
  if (c.phase === 'reconnecting') return t('calls.reconnecting')
  if (c.phase === 'outgoing') return t('calls.calling')
  if (c.mediaState === 'unstable') return t('calls.unstable')
  const people = (call.value?.participants.length ?? 1) > 2 || call.value?.roomSlug ? ` · ${t('chat.members', { n: call.value?.participants.length ?? 1 })}` : ''
  return `${timer.value}${people}`
})

interface Tile {
  key: string
  userId: string
  name: string
  track?: MediaStreamTrack
  showVideo: boolean
  micOn?: boolean
  contain: boolean
  badge?: string
  kind: 'camera' | 'screen'
}

/** 共享的屏幕排在最前、占一整行；其余每人一格 */
const tiles = computed<Tile[]>(() => {
  const screens: Tile[] = []
  const people: Tile[] = []
  for (const p of calls.others) {
    const media = calls.remote[p.id] ?? {}
    const name = family.nameOf(p.userId)
    if (media.screen) screens.push({ key: `${p.id}:screen`, userId: p.userId, name, track: media.screen, showVideo: true, contain: true, badge: t('calls.screen'), kind: 'screen' })
    people.push({ key: p.id, userId: p.userId, name, track: media.video, showVideo: p.camOn && !!media.video, micOn: p.micOn, contain: false, kind: 'camera' })
  }
  return [...screens, ...people]
})
const hasScreen = computed(() => tiles.value.some((x) => x.kind === 'screen'))

const gridStyle = computed(() => {
  const people = tiles.value.filter((x) => x.kind === 'camera').length
  const narrow = width.value < 640
  let cols: number
  if (hasScreen.value) cols = Math.min(Math.max(people, 1), narrow ? 3 : 5)
  else if (narrow) cols = people <= 3 ? 1 : 2
  else cols = people <= 1 ? 1 : people <= 4 ? 2 : 3
  return {
    gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`,
    // 有共享屏幕时：屏幕占大头，人像压成下面一条
    gridTemplateRows: hasScreen.value ? `minmax(0, 4fr) repeat(${Math.ceil(people / cols)}, minmax(0, 1fr))` : undefined,
    gridAutoRows: 'minmax(0, 1fr)',
  }
})

const audios = computed(() =>
  calls.others.flatMap((p) => {
    const track = calls.remote[p.id]?.audio
    return track ? [{ id: p.id, track }] : []
  }),
)

// 自动播放被拦（页面从没被点过，比如从通知直接打开）：给一个按钮
const blocked = ref(false)
const audioEls = ref<InstanceType<typeof CallAudio>[]>([])
function unblock(): void {
  blocked.value = false
  for (const el of audioEls.value) el.resume()
}

const self = computed(() => session.user?.displayName ?? '')
</script>

<template>
  <div>
    <div class="hidden">
      <CallAudio v-for="a in audios" :key="a.id" ref="audioEls" :track="a.track" :participant-id="a.id" @blocked="blocked = true" />
    </div>

    <!-- 最小化：悬浮条，点开回到通话 -->
    <div
      v-if="cur.minimized"
      class="fixed right-3 z-[1500] flex items-center gap-1 rounded-full py-1.5 pl-4 pr-1.5 shadow-lg"
      style="bottom: calc(76px + env(safe-area-inset-bottom)); background: #2a2420; color: #fff"
      data-testid="call-mini"
    >
      <button class="flex items-center gap-2 text-sm" type="button" :title="t('calls.expand')" @click="calls.setMinimized(false)">
        <el-icon :size="18"><FullScreen /></el-icon>
        <span class="max-w-32 truncate">{{ title }}</span>
        <span class="tabular-nums opacity-80">{{ cur.phase === 'active' ? timer : status }}</span>
      </button>
      <button class="ml-2 flex h-9 w-9 items-center justify-center rounded-full" style="background: #e5484d" type="button" :title="t('calls.hangup')" data-testid="call-hangup" @click="calls.hangup()">
        <el-icon :size="18" class="rotate-[135deg]"><PhoneFilled /></el-icon>
      </button>
    </div>

    <div
      v-else
      class="fixed inset-0 z-[1500] flex flex-col"
      style="background: #1b1714; color: #fff; padding-top: env(safe-area-inset-top); padding-bottom: env(safe-area-inset-bottom)"
      role="dialog"
      aria-modal="true"
      data-testid="call-overlay"
      :data-phase="cur.phase"
      :data-media="cur.mediaState"
    >
      <header class="flex items-center gap-3 px-4 py-3">
        <div class="min-w-0 flex-1">
          <div class="truncate text-lg font-semibold" data-testid="call-title">{{ title }}</div>
          <div class="text-sm tabular-nums opacity-70" data-testid="call-status">{{ status }}</div>
        </div>
        <button class="touch-target flex items-center justify-center rounded-full" style="background: rgba(255, 255, 255, 0.12)" type="button" :title="t('calls.minimize')" data-testid="call-minimize" @click="calls.setMinimized(true)">
          <el-icon :size="20"><Minus /></el-icon>
        </button>
      </header>

      <main class="relative min-h-0 flex-1 px-2">
        <!-- 还没有别人：等对方接听 / 客厅里只有自己 -->
        <div v-if="!tiles.length" class="flex h-full flex-col items-center justify-center gap-4">
          <el-avatar :size="112" style="background: var(--pc-primary); font-size: 2.5rem">{{ title.slice(0, 1) }}</el-avatar>
          <div class="opacity-70">{{ cur.phase === 'outgoing' ? t('calls.calling') : cur.phase === 'reconnecting' ? t('calls.reconnecting') : t('calls.waitingOthers') }}</div>
        </div>
        <div v-else class="grid h-full gap-2" :style="gridStyle">
          <ParticipantTile
            v-for="x in tiles"
            :key="x.key"
            :name="x.name"
            :track="x.track"
            :show-video="x.showVideo"
            :mic-on="x.micOn"
            :contain="x.contain"
            :badge="x.badge"
            :user-id="x.userId"
            :kind="x.kind"
            :style="x.kind === 'screen' ? 'grid-column: 1 / -1' : ''"
          />
        </div>

        <!-- 自己的预览 -->
        <ParticipantTile
          v-if="calls.localVideo"
          class="absolute bottom-3 right-4 h-36 w-24 shadow-lg sm:h-32 sm:w-52"
          style="border: 1px solid rgba(255, 255, 255, 0.2)"
          :name="self"
          :track="calls.localVideo"
          show-video
          mirror
          kind="self"
        />
        <div v-if="cur.screenOn" class="absolute left-4 top-2 rounded-full px-3 py-1 text-xs" style="background: var(--pc-primary)" data-testid="call-sharing">
          {{ t('calls.sharingScreen') }}
        </div>
        <button v-if="blocked" class="absolute left-1/2 top-4 -translate-x-1/2 rounded-full px-4 py-2 text-sm font-medium" style="background: #fff; color: #1b1714" type="button" @click="unblock">
          {{ t('calls.enableSound') }}
        </button>
      </main>

      <footer class="flex items-center justify-center gap-4 px-4 py-4 sm:gap-6">
        <button class="pc-call-btn" :class="{ off: !cur.micOn }" type="button" :title="t(cur.micOn ? 'calls.mute' : 'calls.unmute')" data-testid="call-mic" :data-on="cur.micOn" @click="calls.toggleMic()">
          <el-icon :size="24"><component :is="cur.micOn ? Microphone : Mute" /></el-icon>
        </button>
        <button class="pc-call-btn" :class="{ off: !cur.camOn, slash: !cur.camOn }" type="button" :title="t(cur.camOn ? 'calls.camOff' : 'calls.camOn')" data-testid="call-cam" :data-on="cur.camOn" :disabled="cur.phase === 'reconnecting'" @click="calls.toggleCam()">
          <el-icon :size="24"><VideoCamera /></el-icon>
        </button>
        <button v-if="cur.camOn && canFlip" class="pc-call-btn sm:hidden" type="button" :title="t('calls.flip')" data-testid="call-flip" @click="calls.flipCamera()">
          <el-icon :size="24"><Refresh /></el-icon>
        </button>
        <button v-if="canShare" class="pc-call-btn" :class="{ active: cur.screenOn }" type="button" :title="t(cur.screenOn ? 'calls.stopShare' : 'calls.share')" data-testid="call-screen" :data-on="cur.screenOn" :disabled="cur.phase === 'reconnecting'" @click="calls.toggleScreen()">
          <el-icon :size="24"><Monitor /></el-icon>
        </button>
        <button class="pc-call-btn hangup" type="button" :title="t('calls.hangup')" data-testid="call-hangup" @click="calls.hangup()">
          <el-icon :size="26" class="rotate-[135deg]"><PhoneFilled /></el-icon>
        </button>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.pc-call-btn {
  position: relative;
  display: flex;
  height: 56px;
  width: 56px;
  align-items: center;
  justify-content: center;
  border-radius: 9999px;
  background: rgba(255, 255, 255, 0.14);
  color: #fff;
  transition: background 0.15s;
}
.pc-call-btn:disabled {
  opacity: 0.4;
}
/* 关着的开关：白底深色图标，一眼能看出来 */
.pc-call-btn.off {
  background: #fff;
  color: #1b1714;
}
.pc-call-btn.active {
  background: var(--pc-primary);
}
.pc-call-btn.hangup {
  width: 72px;
  background: #e5484d;
}
/* 图标库里没有"摄像头关闭"：画一道斜线 */
.pc-call-btn.slash::after {
  content: '';
  position: absolute;
  height: 2px;
  width: 30px;
  transform: rotate(-45deg);
  border-radius: 2px;
  background: currentColor;
}
</style>

<style>
/* 长辈模式：按钮更大（不带 scoped，要匹配 html 上的 class） */
html.elder [data-testid='call-overlay'] footer button {
  height: 68px;
  width: 68px;
}
html.elder [data-testid='call-overlay'] footer button[data-testid='call-hangup'] {
  width: 88px;
}
</style>
