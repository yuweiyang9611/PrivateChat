<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useElementSize, useEventListener, useMediaQuery, useStorage, useWindowSize } from '@vueuse/core'
import { ElMessage } from 'element-plus'
import { CopyDocument, Microphone, Minus, Monitor, Mute, PhoneFilled, Refresh, VideoCamera, FullScreen } from '@element-plus/icons-vue'
import ParticipantTile from './ParticipantTile.vue'
import ScreenStage from './ScreenStage.vue'
import FloatingCams from './FloatingCams.vue'
import CallAudio from './CallAudio.vue'
import { hasMultipleCameras, screenShareSupported } from './rtc/media'
import { formatDuration } from './format'
import { fitStage } from './stageLayout'
import { attachTrack } from './attachTrack'
import { cornerStyle, isCorner, useFloatDrag, type Corner } from './floating'
import { useCallsStore } from '@/stores/calls'
import { useConversationsStore } from '@/stores/conversations'
import { useFamilyStore } from '@/stores/family'
import { useSessionStore } from '@/stores/session'
import { useUiStore } from '@/stores/ui'

// 通话界面（设计稿 5.5）：全屏，或缩成悬浮条继续看聊天。只在 calls.current 存在时挂载。
const { t } = useI18n()
const calls = useCallsStore()
const conversations = useConversationsStore()
const family = useFamilyStore()
const session = useSessionStore()
const ui = useUiStore()
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

// ---------- 别人共享的屏幕：一块放在大的地方（stage），其余的缩成小格子，点一下换过来 ----------
interface Sharer { userId: string; name: string; track: MediaStreamTrack | null }
/**
 * 正在共享屏幕的人（按人算，不按连接算）。看服务器说的轨道：自己重连时 remote 会清空一阵，画面不能跟着消失；
 * 共享的人重连时会换个新身份、要过一会儿才重新发布，这段时间（最多 10 秒）也把位置留着。画面还没收到时 track 是 null
 */
const sharers = computed<Sharer[]>(() => {
  const out: Sharer[] = []
  for (const p of calls.others) {
    if (out.some((s) => s.userId === p.userId)) continue
    const track = calls.remote[p.id]?.screen ?? null
    if (track || p.tracks.some((x) => x.kind === 'Screen')) out.push({ userId: p.userId, name: family.nameOf(p.userId), track })
  }
  for (const [userId, until] of Object.entries(calls.sharerRejoining)) {
    if (until > now.value && !out.some((s) => s.userId === userId)) out.push({ userId, name: family.nameOf(userId), track: null })
  }
  return out
})
const stageUserId = ref<string | null>(null)
// 新开始共享的人放到大的地方；正在看的那个停了，换成剩下的第一个
watch(() => sharers.value.map((s) => s.userId).join('|'), (ids, before) => {
  const list = ids ? ids.split('|') : []
  const prev = before ? before.split('|') : []
  const added = list.find((id) => !prev.includes(id))
  if (added) stageUserId.value = added
  else if (!stageUserId.value || !list.includes(stageUserId.value)) stageUserId.value = list[0] ?? null
}, { immediate: true })
const stage = computed(() => sharers.value.find((s) => s.userId === stageUserId.value) ?? sharers.value[0] ?? null)
const otherScreens = computed(() => sharers.value.filter((s) => s !== stage.value))

interface Tile { key: string; userId: string; name: string; track?: MediaStreamTrack; showVideo: boolean; micOn?: boolean }
/** 每人一格（摄像头）；正在看谁的屏幕，谁排第一个 */
const people = computed<Tile[]>(() => {
  const list = calls.others.map((p) => {
    const media = calls.remote[p.id] ?? {}
    return { key: p.id, userId: p.userId, name: family.nameOf(p.userId), track: media.video, showVideo: p.camOn && !!media.video, micOn: p.micOn }
  })
  const first = stage.value?.userId
  return first ? [...list.filter((x) => x.userId === first), ...list.filter((x) => x.userId !== first)] : list
})

const gridStyle = computed(() => {
  const n = people.value.length
  const cols = width.value < 640 ? (n <= 3 ? 1 : 2) : n <= 1 ? 1 : n <= 4 ? 2 : 3
  return { gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`, gridAutoRows: 'minmax(0, 1fr)' }
})

// 画面按真实比例尽量大：宽的窗口人像放右边一列（side），窄的放下面（stack）
const layoutEl = ref<HTMLElement | null>(null)
const { width: lw, height: lh } = useElementSize(layoutEl)
const stageAspect = ref(16 / 9)
watch(stageUserId, () => { stageAspect.value = 16 / 9 })
/**
 * 摆法用到的尺寸：按页面的根字号算（长辈模式 125%，浏览器默认字号调大了也跟着变），和 DOM 里那些按 rem 定的大小对得上：
 * 窄屏时画面上面那一行（按钮 2.75rem，长辈模式至少 56）、下面至少留给人像 7.5rem（自己的预览 7rem 高 + 0.5rem 边距）
 */
const rootPx = computed(() => {
  void ui.elderMode
  return Number.parseFloat(getComputedStyle(document.documentElement).fontSize) || 16
})
const opts = computed(() => {
  const rem = rootPx.value
  return ui.elderMode
    ? { gap: 8, capH: Math.max(56, Math.ceil(2.75 * rem)), colMin: 200, colMax: 400, peopleMin: Math.ceil(7.5 * rem) }
    : { gap: 8, capH: Math.max(44, Math.ceil(2.75 * rem)), colMin: 160, colMax: 360, peopleMin: Math.ceil(7.5 * rem) }
})
const fit = computed(() => fitStage(lw.value, lh.value, stageAspect.value, opts.value))
const layoutStyle = computed(() => {
  const f = fit.value
  if (f.mode === 'side') {
    return { gridTemplateColumns: `${f.fw}px ${f.colW}px`, gridTemplateRows: 'auto minmax(0, 1fr)', gridTemplateAreas: '"stage caption" "stage people"', justifyContent: 'center' }
  }
  return { gridTemplateColumns: 'minmax(0, 1fr)', gridTemplateRows: `${opts.value.capH}px ${f.fh}px minmax(0, 1fr)`, gridTemplateAreas: '"caption" "stage" "people"' }
})
const stackPeopleStyle = computed(() => {
  const n = otherScreens.value.length + people.value.length
  return { gridTemplateColumns: `repeat(${n <= 2 ? 1 : 2}, minmax(0, 1fr))`, gridAutoRows: 'minmax(72px, 1fr)' }
})
const stageNotice = computed(() => (cur.value.phase === 'reconnecting' || cur.value.mediaState === 'unstable' ? status.value : null))

// ---------- 大图：共享的屏幕铺满整个通话界面 ----------
// 点"全屏"时顺便让整个页面全屏（document.documentElement，不是只让画面全屏：来电窗、提示都在通话界面外面，要照样看得见）。
// iPhone 上没有全屏，就只在页面里铺满。只退出自己进的全屏（用户自己按 F11 的不管）
const expanded = ref(false)
const expandedBy = ref<'user' | 'auto' | null>(null)
let fsByUs = false
let locked = false
const coarse = useMediaQuery('(pointer: coarse)')
type FsDoc = Document & { webkitFullscreenElement?: Element | null; webkitExitFullscreen?: () => Promise<void> | void }
type FsEl = HTMLElement & { webkitRequestFullscreen?: (o?: FullscreenOptions) => Promise<void> | void }
type Lockable = ScreenOrientation & { lock?: (o: string) => Promise<void>; unlock?: () => void }
const fullscreenElement = () => document.fullscreenElement ?? (document as FsDoc).webkitFullscreenElement ?? null

function enterExpanded(by: 'user' | 'auto'): void {
  if (expanded.value) return
  expanded.value = true
  expandedBy.value = by
  if (by !== 'user') return
  // 要在点击里同步调用，浏览器才认这是用户点的
  const el = document.documentElement as FsEl
  const request = el.requestFullscreen ?? el.webkitRequestFullscreen
  if (!request || fullscreenElement()) return
  fsByUs = true
  Promise.resolve(request.call(el, { navigationUI: 'hide' }))
    .then(() => {
      // 全屏还没进去大图就收起了：进去了也马上退出来
      if (!expanded.value || !fsByUs) {
        leaveFullscreen()
        return
      }
      // 手机上看横的屏幕：转成横屏（安卓可以，转不了就算了）
      if (!coarse.value || stageAspect.value <= 1.2) return
      ;(screen.orientation as Lockable | undefined)?.lock?.('landscape').then(() => {
        if (expanded.value) locked = true
        else unlockOrientation()
      }).catch(() => undefined)
    })
    .catch(() => { fsByUs = false })
}

function unlockOrientation(): void {
  try { (screen.orientation as Lockable | undefined)?.unlock?.() } catch { /* 不支持 */ }
}

function leaveFullscreen(): void {
  if (!fullscreenElement()) return
  const doc = document as FsDoc
  void Promise.resolve((doc.exitFullscreen ?? doc.webkitExitFullscreen)?.call(doc)).catch(() => undefined)
}

function exitExpanded(): void {
  expanded.value = false
  expandedBy.value = null
  floatCollapsed.value = false
  if (locked) {
    locked = false
    unlockOrientation()
  }
  if (fsByUs) {
    fsByUs = false
    leaveFullscreen()
  }
}
/** 大图里摄像头小窗收没收起：换着看别人的屏幕时不变，退出大图就还原 */
const floatCollapsed = ref(false)

useEventListener(window, 'keydown', (e: KeyboardEvent) => { if (e.key === 'Escape' && expanded.value) exitExpanded() })
// 浏览器自己退出了全屏（按 Esc、安卓返回键）：大图也收起来
useEventListener(document, ['fullscreenchange', 'webkitfullscreenchange'], () => { if (fsByUs && !fullscreenElement()) exitExpanded() })
watch(stage, (s) => { if (!s) exitExpanded() })
watch(() => cur.value.minimized, (m) => { if (m) exitExpanded() })
onBeforeUnmount(exitExpanded)

// 手机横过来看别人的屏幕：自动进大图（页面里铺满，不能自动全屏）；竖回去收起。自己点开的不跟着转屏收起
const phoneLandscape = useMediaQuery('(pointer: coarse) and (orientation: landscape) and (max-height: 500px)')
watch(() => !!stage.value && phoneLandscape.value && !cur.value.minimized, (want) => {
  if (want && !expanded.value) enterExpanded('auto')
  else if (!want && expandedBy.value === 'auto') exitExpanded()
}, { immediate: true })

const layout = computed(() => (stage.value ? fit.value.mode : 'grid'))

// ---------- 画中画：把共享的画面放进浏览器的系统小窗（切到别的应用也浮在最上面） ----------
// 用一个一直挂着的 video（不跟着大图、最小化、换人重建），不然一最小化小窗就黑了。
// 要真的渲染出来（1 px、透明），有的浏览器不让没渲染的元素进画中画
const pipSupported = typeof document !== 'undefined' && document.pictureInPictureEnabled === true
const pipVideo = ref<HTMLVideoElement | null>(null)
const pipTrack = computed(() => (pipSupported ? stage.value?.track ?? null : null))
watch(() => [pipTrack.value, pipVideo.value] as const, ([track, el]) => {
  attachTrack(el, track)
  // 共享停了（不是重连那一下）：画中画也收起来，别留一个黑窗口
  if (!track && !stage.value && el && document.pictureInPictureElement === el) void document.exitPictureInPicture().catch(() => undefined)
}, { immediate: true, flush: 'post' })
async function openPip(): Promise<void> {
  const el = pipVideo.value
  if (!el || !pipTrack.value) return
  try {
    if (document.pictureInPictureElement === el) await document.exitPictureInPicture()
    else await el.requestPictureInPicture()
  } catch {
    ElMessage.warning(t('calls.pipFailed'))
  }
}
onBeforeUnmount(() => {
  if (pipVideo.value && document.pictureInPictureElement === pipVideo.value) void document.exitPictureInPicture().catch(() => undefined)
})

// ---------- 最小化以后的浮窗：有人共享就放共享的画面，不然放对方的摄像头；能拖到四个角，点一下回到通话 ----------
const miniEl = ref<HTMLElement | null>(null)
const miniCorner = useStorage<Corner>('pc.call.miniCorner', 'br')
if (!isCorner(miniCorner.value)) miniCorner.value = 'br'
const miniDrag = useFloatDrag(miniEl, miniCorner)
const miniStyle = computed(() => {
  if (miniDrag.drag.value) return { left: `${miniDrag.drag.value.x}px`, top: `${miniDrag.drag.value.y}px` }
  // 下面让开手机底部的导航栏
  return cornerStyle(miniCorner.value, {
    top: 'calc(12px + env(safe-area-inset-top))',
    right: 'calc(12px + env(safe-area-inset-right))',
    bottom: 'calc(76px + env(safe-area-inset-bottom))',
    left: 'calc(12px + env(safe-area-inset-left))',
  })
})
const miniVideo = computed(() => {
  if (stage.value?.track) return { track: stage.value.track, kind: 'screen' as const }
  const p = people.value.find((x) => x.showVideo && x.track)
  return p?.track ? { track: p.track, kind: 'camera' as const } : null
})
const miniVideoEl = ref<HTMLVideoElement | null>(null)
watch(() => [cur.value.minimized ? miniVideo.value?.track ?? null : null, miniVideoEl.value] as const, ([track, el]) => attachTrack(el, track), { immediate: true, flush: 'post' })
function restore(): void {
  if (miniDrag.clicked()) calls.setMinimized(false)
}
function hangupFromMini(): void {
  if (miniDrag.clicked()) void calls.hangup()
}

// iPhone：别让双指捏合把整个通话界面放大（大图里的放大由 ScreenStage 自己处理）
const overlayEl = ref<HTMLElement | null>(null)
useEventListener(overlayEl, 'gesturestart', (e: Event) => e.preventDefault(), { passive: false })

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
    <!-- 画中画用的画面（见 openPip） -->
    <video v-if="pipSupported" ref="pipVideo" class="pc-pip-source" autoplay playsinline muted aria-hidden="true" data-testid="call-pip-video" />

    <!-- 最小化：浮窗（有画面时带一小块画面）+ 悬浮条，能拖到四个角，点开回到通话 -->
    <div
      v-if="cur.minimized"
      ref="miniEl"
      class="pc-mini fixed z-[1500] flex flex-col gap-2"
      :class="miniCorner[1] === 'l' ? 'items-start' : 'items-end'"
      :style="miniStyle"
      data-testid="call-mini"
      :data-corner="miniCorner"
      v-on="miniDrag.handlers"
    >
      <button
        v-if="miniVideo"
        class="pc-mini-video"
        :class="miniVideo.kind === 'screen' ? 'is-screen' : 'is-camera'"
        type="button"
        :title="t('calls.expand')"
        data-testid="call-mini-video"
        :data-kind="miniVideo.kind"
        @click="restore"
      >
        <video ref="miniVideoEl" class="h-full w-full" :class="miniVideo.kind === 'screen' ? 'object-contain' : 'object-cover'" autoplay playsinline muted />
      </button>
      <div class="flex items-center gap-1 rounded-full py-1.5 pl-4 pr-1.5 shadow-lg" style="background: #2a2420; color: #fff">
        <button class="flex items-center gap-2 text-sm" type="button" :title="t('calls.expand')" @click="restore">
          <el-icon :size="18"><FullScreen /></el-icon>
          <span class="max-w-32 truncate">{{ title }}</span>
          <span class="tabular-nums opacity-80">{{ cur.phase === 'active' ? timer : status }}</span>
        </button>
        <button class="ml-2 flex h-9 w-9 items-center justify-center rounded-full" style="background: #e5484d" type="button" :title="t('calls.hangup')" data-testid="call-hangup" @click="hangupFromMini">
          <el-icon :size="18" class="rotate-[135deg]"><PhoneFilled /></el-icon>
        </button>
      </div>
    </div>

    <div
      v-else
      ref="overlayEl"
      class="fixed inset-0 z-[1500] flex flex-col"
      style="background: #1b1714; color: #fff; padding: env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom) env(safe-area-inset-left); touch-action: pan-x pan-y"
      role="dialog"
      aria-modal="true"
      data-testid="call-overlay"
      :data-phase="cur.phase"
      :data-media="cur.mediaState"
      :data-layout="layout"
      :data-expanded="expanded"
    >
      <header class="flex items-center gap-3 px-4 py-3" :inert="expanded">
        <div class="min-w-0 flex-1">
          <div class="truncate text-lg font-semibold" data-testid="call-title">{{ title }}</div>
          <div class="text-sm tabular-nums opacity-70" data-testid="call-status">{{ status }}</div>
        </div>
        <button class="touch-target flex items-center justify-center rounded-full" style="background: rgba(255, 255, 255, 0.12)" type="button" :title="t('calls.minimize')" data-testid="call-minimize" @click="calls.setMinimized(true)">
          <el-icon :size="20"><Minus /></el-icon>
        </button>
      </header>

      <main class="relative flex min-h-0 flex-1 flex-col gap-2 px-2">
        <!-- 我在共享：一直提示着，旁边就是停止按钮（白底上的橙色字看不清，用深色字） -->
        <div
          v-if="cur.screenOn"
          class="flex shrink-0 items-center gap-3 rounded-xl px-4 py-2 text-base font-medium"
          style="background: var(--pc-primary); color: #2b211a"
          data-testid="call-sharing"
          :inert="expanded"
        >
          <el-icon :size="20"><Monitor /></el-icon>
          <span class="min-w-0 flex-1">{{ t('calls.sharingScreen') }}</span>
          <button
            class="touch-target h-11 shrink-0 rounded-full px-5 font-semibold"
            style="background: #fff; color: #1b1714"
            type="button"
            data-testid="call-sharing-stop"
            :disabled="cur.phase === 'reconnecting'"
            @click="calls.toggleScreen()"
          >
            {{ t('calls.stopShare') }}
          </button>
        </div>

        <!-- 还没有别人：等对方接听 / 客厅里只有自己 -->
        <div v-if="!people.length" class="flex min-h-0 flex-1 flex-col items-center justify-center gap-4">
          <el-avatar :size="112" style="background: var(--pc-primary); font-size: 2.5rem">{{ title.slice(0, 1) }}</el-avatar>
          <div class="opacity-70">{{ cur.phase === 'outgoing' ? t('calls.calling') : cur.phase === 'reconnecting' ? t('calls.reconnecting') : t('calls.waitingOthers') }}</div>
        </div>

        <!-- 有人在共享屏幕：画面放大的地方，谁的屏幕 + 全屏按钮、人像放旁边或下面 -->
        <div v-else-if="stage" ref="layoutEl" class="pc-stage-layout min-h-0 min-w-0 flex-1" :style="layoutStyle">
          <div
            class="pc-area-caption min-w-0"
            :class="fit.mode === 'side' ? 'flex flex-col gap-2' : 'flex items-center justify-between gap-2 px-1'"
            :style="fit.mode === 'stack' ? { height: `${opts.capH}px` } : undefined"
            data-testid="call-stage-caption"
            :inert="expanded"
          >
            <div class="flex min-w-0 flex-1 items-center gap-2">
              <el-icon :size="18" class="shrink-0"><Monitor /></el-icon>
              <span class="truncate text-base font-medium">{{ t('calls.screenOf', { name: stage.name }) }}</span>
            </div>
            <!-- 两个按钮并排一行（右边一列里也是）：竖着叠会把下面的人像挤没 -->
            <div class="flex shrink-0 gap-2" :class="fit.mode === 'side' ? 'w-full' : ''">
              <button
                class="touch-target flex h-11 min-w-0 items-center justify-center gap-1 rounded-full px-3 text-sm"
                :class="fit.mode === 'side' ? 'flex-1' : 'shrink-0'"
                style="background: rgba(255, 255, 255, 0.14)"
                type="button"
                data-testid="call-fullscreen"
                @click="enterExpanded('user')"
              >
                <el-icon :size="18" class="shrink-0"><FullScreen /></el-icon>
                <span class="truncate">{{ t('calls.fullscreen') }}</span>
              </button>
              <button
                v-if="pipSupported && pipTrack"
                class="touch-target flex h-11 min-w-0 items-center justify-center gap-1 rounded-full px-3 text-sm"
                :class="fit.mode === 'side' ? 'flex-1' : 'shrink-0'"
                style="background: rgba(255, 255, 255, 0.14)"
                type="button"
                :title="t('calls.pipHint')"
                data-testid="call-pip"
                @click="openPip"
              >
                <el-icon :size="18" class="shrink-0"><CopyDocument /></el-icon>
                <span class="truncate">{{ t('calls.pip') }}</span>
              </button>
            </div>
          </div>

          <ScreenStage
            :key="stage.userId"
            class="pc-area-stage"
            :user-id="stage.userId"
            :name="stage.name"
            :track="stage.track"
            :frame-w="fit.fw"
            :frame-h="fit.fh"
            :initial-aspect="stageAspect"
            :expanded="expanded"
            :notice="stageNotice"
            @expand="enterExpanded('user')"
            @collapse="exitExpanded()"
            @aspect="stageAspect = $event"
          >
            <template #float>
              <FloatingCams v-model:collapsed="floatCollapsed" :people="people" />
            </template>
          </ScreenStage>

          <!-- 宽：右边一列，从上往下 -->
          <div v-if="fit.mode === 'side'" class="pc-area-people flex min-h-0 flex-col gap-2 overflow-y-auto" :inert="expanded">
            <button
              v-for="s in otherScreens"
              :key="`screen:${s.userId}`"
              class="aspect-video w-full shrink-0"
              type="button"
              :title="t('calls.switchScreen', { name: s.name })"
              data-testid="call-switch-screen"
              @click="stageUserId = s.userId"
            >
              <ParticipantTile class="h-full w-full" kind="screen" contain compact :name="t('calls.screenOf', { name: s.name })" :track="s.track" :show-video="!!s.track" :user-id="s.userId" />
            </button>
            <ParticipantTile
              v-for="x in people"
              :key="x.key"
              class="aspect-video w-full shrink-0"
              compact
              :name="x.name"
              :track="x.track"
              :show-video="x.showVideo"
              :mic-on="x.micOn"
              :user-id="x.userId"
              kind="camera"
            />
            <ParticipantTile
              v-if="calls.localVideo"
              class="aspect-video w-1/2 shrink-0 self-end"
              style="border: 1px solid rgba(255, 255, 255, 0.2)"
              compact
              :name="self"
              :track="calls.localVideo"
              show-video
              mirror
              kind="self"
            />
          </div>
          <!-- 窄：画面下面一排，自己的预览放在这一排的角上（不压着共享的画面） -->
          <div v-else class="pc-area-people relative min-h-0" :inert="expanded">
            <div class="grid h-full gap-2 overflow-y-auto" :style="stackPeopleStyle">
              <button
                v-for="s in otherScreens"
                :key="`screen:${s.userId}`"
                class="min-h-0"
                type="button"
                :title="t('calls.switchScreen', { name: s.name })"
                data-testid="call-switch-screen"
                @click="stageUserId = s.userId"
              >
                <ParticipantTile class="h-full w-full" kind="screen" contain compact :name="t('calls.screenOf', { name: s.name })" :track="s.track" :show-video="!!s.track" :user-id="s.userId" />
              </button>
              <ParticipantTile
                v-for="x in people"
                :key="x.key"
                compact
                :name="x.name"
                :track="x.track"
                :show-video="x.showVideo"
                :mic-on="x.micOn"
                :user-id="x.userId"
                kind="camera"
              />
            </div>
            <ParticipantTile
              v-if="calls.localVideo"
              class="!absolute bottom-2 right-2 h-28 w-20 shadow-lg sm:h-24 sm:w-40"
              style="border: 1px solid rgba(255, 255, 255, 0.2)"
              compact
              :name="self"
              :track="calls.localVideo"
              show-video
              mirror
              kind="self"
            />
          </div>
        </div>

        <!-- 没人共享：每人一格 -->
        <div v-else class="grid min-h-0 flex-1 gap-2" :style="gridStyle">
          <ParticipantTile
            v-for="x in people"
            :key="x.key"
            :name="x.name"
            :track="x.track"
            :show-video="x.showVideo"
            :mic-on="x.micOn"
            :user-id="x.userId"
            kind="camera"
          />
        </div>

        <!-- 自己的预览（有人共享屏幕时放在人像那一列 / 那一排里） -->
        <ParticipantTile
          v-if="calls.localVideo && !(stage && people.length)"
          class="!absolute bottom-3 right-4 h-36 w-24 shadow-lg sm:h-32 sm:w-52"
          style="border: 1px solid rgba(255, 255, 255, 0.2)"
          :name="self"
          :track="calls.localVideo"
          show-video
          mirror
          kind="self"
        />
        <button v-if="blocked" class="absolute left-1/2 top-4 z-20 -translate-x-1/2 rounded-full px-4 py-2 text-sm font-medium" style="background: #fff; color: #1b1714" type="button" @click="unblock">
          {{ t('calls.enableSound') }}
        </button>
      </main>

      <!-- 窄屏（360 px 的安卓、长辈模式）间距收紧；还放不下时按钮一起等比缩小，保持圆形 -->
      <footer class="flex items-center justify-center gap-2 px-2 py-4 min-[400px]:gap-4 min-[400px]:px-4 sm:gap-6" :inert="expanded">
        <button class="pc-call-btn" :class="{ off: !cur.micOn }" type="button" :title="t(cur.micOn ? 'calls.mute' : 'calls.unmute')" data-testid="call-mic" :data-on="cur.micOn" @click="calls.toggleMic()">
          <el-icon :size="24"><component :is="cur.micOn ? Microphone : Mute" /></el-icon>
        </button>
        <button class="pc-call-btn" :class="{ off: !cur.camOn, slash: !cur.camOn }" type="button" :title="t(cur.camOn ? 'calls.camOff' : 'calls.camOn')" data-testid="call-cam" :data-on="cur.camOn" :disabled="cur.phase === 'reconnecting' || calls.camBusy" @click="calls.toggleCam()">
          <el-icon :size="24"><VideoCamera /></el-icon>
        </button>
        <!-- 有前后摄像头的设备（手机、平板，横竖都算）才有翻转；用鼠标的电脑不显示 -->
        <button v-if="cur.camOn && canFlip" class="pc-call-btn [@media(pointer:fine)]:hidden" type="button" :title="t('calls.flip')" data-testid="call-flip" :disabled="cur.phase === 'reconnecting' || calls.camBusy" @click="calls.flipCamera()">
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
.pc-stage-layout {
  display: grid;
  gap: 8px;
  overflow: hidden;
}
.pc-area-caption {
  grid-area: caption;
}
.pc-area-stage {
  grid-area: stage;
  align-self: center;
  justify-self: center;
}
.pc-area-people {
  grid-area: people;
}
/* 画中画用的画面：渲染着但看不见、点不到 */
.pc-pip-source {
  position: fixed;
  left: 0;
  bottom: 0;
  width: 2px;
  height: 2px;
  opacity: 0;
  pointer-events: none;
}
/* 最小化后的浮窗：拖的时候别滚页面、别选中文字 */
.pc-mini {
  touch-action: none;
  user-select: none;
  -webkit-user-select: none;
}
.pc-mini-video {
  overflow: hidden;
  border-radius: 14px;
  background: #000;
  border: 1px solid rgba(255, 255, 255, 0.25);
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
}
.pc-mini-video.is-screen {
  width: 192px;
  aspect-ratio: 16 / 9;
}
.pc-mini-video.is-camera {
  width: 112px;
  aspect-ratio: 3 / 4;
}
@media (max-width: 639px) {
  .pc-mini-video.is-screen {
    width: 160px;
  }
  .pc-mini-video.is-camera {
    width: 96px;
  }
}
/* 放不下时一起等比缩小（宽度按比例让，高度跟着 aspect-ratio 走，不会挤成椭圆），最小 44 px */
.pc-call-btn {
  position: relative;
  display: flex;
  flex: 0 1 56px;
  width: 56px;
  min-width: 44px;
  height: auto;
  aspect-ratio: 1;
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
  flex-basis: 72px;
  width: 72px;
  min-width: 56px;
  aspect-ratio: 72 / 56;
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
  flex-basis: 68px;
  width: 68px;
  min-width: 52px;
}
html.elder [data-testid='call-overlay'] footer button[data-testid='call-hangup'] {
  flex-basis: 88px;
  width: 88px;
  min-width: 68px;
  aspect-ratio: 88 / 68;
}
</style>
