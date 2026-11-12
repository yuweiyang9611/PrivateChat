<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ArrowDown, Close, EditPen, FullScreen } from '@element-plus/icons-vue'
import { useFamilyStore } from '@/stores/family'
import { useRemoteStore } from '@/stores/remote'
import { RemoteClient, webCodecsSupported, type LinkState, type RemoteStats } from './RemoteClient'
import { encode, MAX_TEXT_BYTES, modifiersOf, normalizePoint, Quality, scanCodeOf, SpecialKey, StatusCode, utf8Length, WheelAccumulator, type MonitorInfo, type QualityPreset } from './protocol'
import { formatKbps } from './format'

const props = defineProps<{ id: string }>()
const { t } = useI18n()
const router = useRouter()
const remote = useRemoteStore()
const family = useFamilyStore()

const root = ref<HTMLDivElement>()
const canvas = ref<HTMLCanvasElement>()
const stage = ref<HTMLDivElement>()
const textInput = ref<{ focus: () => void }>()
const notFound = ref(false)
const link = ref<LinkState>('connecting')
const video = ref<{ width: number; height: number; fps: number; monitor: number } | null>(null)
const monitors = ref<MonitorInfo[]>([])
const stats = ref<RemoteStats | null>(null)
const blocked = ref<{ code: number; message: string } | null>(null)
const unsupported = ref(!webCodecsSupported())
/** 这个窗口的控制连接被别的窗口 / 设备顶掉了（会话还在，在那边用着） */
const replaced = ref(false)
const quality = ref<QualityPreset>(Quality.Smooth)
const fullscreen = ref(false)
const textOpen = ref(false)
const textValue = ref('')
const now = ref(Date.now())
let client: RemoteClient | null = null
let tick: number | undefined

const session = computed(() => remote.sessions[props.id])
const control = computed(() => session.value?.mode === 'Control')
const state = computed(() => session.value?.state)
const secondsLeft = computed(() => (session.value ? Math.max(0, Math.ceil((new Date(session.value.expiresAt).getTime() - now.value) / 1000)) : 0))
const ownerName = computed(() => (session.value ? family.nameOf(session.value.deviceOwnerId) : ''))
const isMac = /Mac|iPhone|iPad/.test(navigator.platform)
const endReason = computed(() => {
  const r = session.value?.endReason
  return r && ['declined', 'timeout', 'controller', 'agent', 'stopped', 'revoked', 'disconnected', 'replaced'].includes(r) ? t(`remote.reasons.${r}`) : t('remote.viewer.ended')
})
const statsText = computed(() => {
  if (!video.value || !stats.value) return ''
  return `${video.value.width}×${video.value.height} · ${stats.value.decoded} fps · ${formatKbps(stats.value.kbps)} · ${stats.value.rtt} ms`
})

/** 全屏时提示要放在全屏元素里面，否则看不见 */
function toast(type: 'success' | 'warning' | 'error', message: string): void {
  ElMessage({ type, message, appendTo: fullscreen.value ? root.value : undefined })
}

onMounted(async () => {
  void family.load()
  tick = window.setInterval(() => (now.value = Date.now()), 1000)
  document.addEventListener('fullscreenchange', onFullscreenChange)
  document.addEventListener('paste', onPaste)
  document.addEventListener('visibilitychange', releaseAll)
  window.addEventListener('blur', releaseAll)
  try {
    await remote.get(props.id)
  } catch {
    notFound.value = true
  }
})

onBeforeUnmount(() => {
  window.clearInterval(tick)
  document.removeEventListener('fullscreenchange', onFullscreenChange)
  document.removeEventListener('paste', onPaste)
  document.removeEventListener('visibilitychange', releaseAll)
  window.removeEventListener('blur', releaseAll)
  stopClient()
  if (document.fullscreenElement) void document.exitFullscreen().catch(() => undefined)
})

// 在应用里离开这个页面（返回、点导航）就结束远程，别让对方电脑上的红条再挂 30 秒；刷新页面则会接着连。
// 已经在别的窗口 / 设备上接着用了的不结束
onBeforeRouteLeave(() => {
  if (session.value && !session.value.endedAt && !replaced.value) void remote.end(props.id).catch(() => undefined)
})

// "再试一次"换成了新会话（同一个组件，参数变了）：清掉上一次的画面状态
watch(() => props.id, (id, old) => {
  if (id === old) return
  stopClient()
  notFound.value = false
  replaced.value = false
  link.value = 'connecting'
  video.value = null
  monitors.value = []
  stats.value = null
  blocked.value = null
  quality.value = Quality.Smooth
  textOpen.value = false
  void remote.get(id).catch(() => (notFound.value = true))
})

watch([state, canvas], async ([s]) => {
  if (s === 'Active' && !client && !unsupported.value && !replaced.value) {
    await nextTick()
    // 等待期间可能已经被另一次触发启动过（挂载时 state 和 canvas 先后变化）
    if (client || state.value !== 'Active' || !canvas.value || unsupported.value || replaced.value) return
    startClient()
  } else if (s === 'Ended') {
    stopClient()
  }
}, { immediate: true })

function startClient(): void {
  if (!canvas.value) return
  const c = new RemoteClient(props.id, canvas.value, {
    state: (s) => (link.value = s),
    config: (v) => (video.value = v),
    monitors: (m) => (monitors.value = m),
    stats: (s) => (stats.value = s),
    status: (code, message) => {
      if (code === StatusCode.Unsupported) toast('warning', message)
      else blocked.value = code === StatusCode.Ok ? null : { code, message }
    },
    clipboard: (text) => {
      navigator.clipboard?.writeText(text).then(() => toast('success', t('remote.viewer.clipboardIn')), () => undefined)
    },
    ended: (why) => {
      if (client !== c) return
      client = null
      if (why === 'replaced') replaced.value = true
      void remote.get(props.id).catch(() => undefined)
    },
    unsupported: () => (unsupported.value = true),
  })
  client = c
  c.connect()
  void nextTick(() => stage.value?.focus())
}

function stopClient(): void {
  client?.close()
  client = null
  sentButtons.clear()
}

/** 在这个窗口接着用（把别处的控制连接顶掉） */
function takeOver(): void {
  replaced.value = false
  link.value = 'connecting'
  if (state.value === 'Active' && !client) startClient()
}

async function end(): Promise<void> {
  try {
    await remote.end(props.id)
  } catch (e) {
    toast('error', (e as Error).message)
  }
}

async function again(): Promise<void> {
  const s = session.value
  if (!s) return
  let pin: string | undefined
  if (s.consent === 'Unattended') {
    // 上一次是凭 PIN 进来的（电脑前多半没人点同意）：再要一次 PIN
    try {
      pin = (await ElMessageBox.prompt(t('remote.pinPrompt'), t('remote.pinTitle'), {
        inputType: 'password',
        inputPattern: /^\d{6,12}$/,
        inputErrorMessage: '6 到 12 位数字',
        confirmButtonText: t('remote.control'),
        cancelButtonText: t('common.cancel'),
        appendTo: fullscreen.value ? root.value : undefined,
      })).value
    } catch {
      return
    }
  }
  try {
    const next = await remote.start(s.targetDeviceId, s.mode, pin)
    await router.replace(`/remote/${next.id}`)
  } catch (e) {
    toast('error', (e as Error).message)
  }
}

function setQuality(q: QualityPreset): void {
  quality.value = q
  client?.setQuality(q)
}

function selectMonitor(index: number): void {
  client?.selectMonitor(index)
}

function special(command: string): void {
  const id = { win: SpecialKey.Win, taskManager: SpecialKey.TaskManager, altTab: SpecialKey.AltTab, printScreen: SpecialKey.PrintScreen }[command]
  if (id) client?.send(encode.specialKey(id))
  stage.value?.focus()
}

function sendText(e?: Event): void {
  if ((e as KeyboardEvent | undefined)?.isComposing) return
  if (textValue.value) {
    if (utf8Length(textValue.value) > MAX_TEXT_BYTES) toast('warning', t('remote.viewer.textTruncated'))
    client?.send(encode.text(textValue.value))
  }
  textValue.value = ''
}

async function toggleFullscreen(): Promise<void> {
  if (document.fullscreenElement) {
    await document.exitFullscreen().catch(() => undefined)
    return
  }
  await root.value?.requestFullscreen().catch(() => undefined)
  // 全屏时锁住键盘：Esc、Alt+Tab、Win 也能发给对方（只有 Chromium 支持）
  const keyboard = (navigator as Navigator & { keyboard?: { lock?: () => Promise<void> } }).keyboard
  await keyboard?.lock?.().catch(() => undefined)
  stage.value?.focus()
}

function onFullscreenChange(): void {
  fullscreen.value = !!document.fullscreenElement
  if (!fullscreen.value) (navigator as Navigator & { keyboard?: { unlock?: () => void } }).keyboard?.unlock?.()
}

// ---- 鼠标 / 触摸 ----

const wheel = new WheelAccumulator()
let pendingMove: { x: number; y: number } | null = null
let moveFrame = 0
/** 已经发给对方"按下"的鼠标键（协议编号）；按 PointerEvent.buttons 的位图同步，同时按住两个键也不会漏掉松开 */
const sentButtons = new Set<number>()
/** [PointerEvent.buttons 的位, 协议里的键]：左 1、右 2、中 4、后退 8、前进 16 */
const BUTTON_BITS: ReadonlyArray<readonly [number, number]> = [[1, 0], [4, 1], [2, 2], [8, 3], [16, 4]]

function point(e: { clientX: number; clientY: number }): { x: number; y: number } | null {
  const c = canvas.value
  if (!c) return null
  return normalizePoint(e.clientX, e.clientY, c.getBoundingClientRect(), c.width, c.height)
}

function flushMove(): void {
  if (moveFrame) cancelAnimationFrame(moveFrame)
  moveFrame = 0
  if (pendingMove) client?.send(encode.mouseMove(pendingMove.x, pendingMove.y), true)
  pendingMove = null
}

/** 按 e.buttons 发按下 / 松开；allowPress=false 时只松开（指针在黑边上不新按下任何键） */
function syncButtons(buttons: number, allowPress: boolean): void {
  for (const [bit, button] of BUTTON_BITS) {
    const pressed = (buttons & bit) !== 0
    if (pressed && allowPress && !sentButtons.has(button)) {
      sentButtons.add(button)
      client?.send(encode.mouseButton(button, true))
    } else if (!pressed && sentButtons.has(button)) {
      sentButtons.delete(button)
      client?.send(encode.mouseButton(button, false))
    }
  }
}

function onPointerMove(e: PointerEvent): void {
  if (!control.value) return
  const p = point(e)
  if (p) {
    pendingMove = p
    moveFrame ||= requestAnimationFrame(flushMove)
  }
  // 按住一个键时再按 / 松另一个键，浏览器只发 pointermove
  if (sentButtons.size || e.buttons) {
    if (p) flushMove()
    syncButtons(e.buttons, !!p)
  }
}

function onPointerDown(e: PointerEvent): void {
  if (!control.value) return
  e.preventDefault()
  stage.value?.focus()
  const p = point(e)
  // 点在画面外的黑边上：不在对方电脑上点击（否则会点在对方鼠标上一次停留的地方）
  if (!p && !sentButtons.size) return
  canvas.value?.setPointerCapture(e.pointerId)
  if (p) pendingMove = p
  flushMove()
  syncButtons(e.buttons, !!p)
}

function onPointerUp(e: PointerEvent): void {
  if (!control.value) return
  // 鼠标侧键（后退 / 前进）在松开时会让浏览器后退，拦下来：它是发给对方电脑的
  e.preventDefault()
  const p = point(e)
  if (p) pendingMove = p
  flushMove()
  syncButtons(e.type === 'pointercancel' ? 0 : e.buttons, false)
}

function onMouseUp(e: MouseEvent): void {
  if (e.button === 3 || e.button === 4) e.preventDefault()
}

function onWheel(e: WheelEvent): void {
  if (!control.value) return
  e.preventDefault()
  const r = wheel.push(e)
  if (r) client?.send(encode.wheel(r.dx, r.dy))
}

// ---- 键盘 ----

const MODIFIER_CODES = new Set(['ShiftLeft', 'ShiftRight', 'ControlLeft', 'ControlRight', 'AltLeft', 'AltRight', 'MetaLeft', 'MetaRight', 'CapsLock'])

/** Mac 上的 ⌘ 当 Ctrl 发（⌘C / ⌘V 在对方 Windows 上就是复制粘贴） */
function scanOf(e: KeyboardEvent): number | undefined {
  if (isMac && e.code === 'MetaLeft') return scanCodeOf('ControlLeft')
  if (isMac && e.code === 'MetaRight') return scanCodeOf('ControlRight')
  return scanCodeOf(e.code)
}

/** Mac 上按住 ⌘ 时松开的键浏览器不发 keyup：按下后立刻补一个松开，不然这个键在对方电脑上一直按着 */
function tapsOnly(e: KeyboardEvent): boolean {
  return isMac && e.metaKey && !MODIFIER_CODES.has(e.code)
}

let pendingPaste: number | null = null
let pasteTimer: number | undefined

function onKeyDown(e: KeyboardEvent): void {
  if (!control.value || e.isComposing) return
  const scan = scanOf(e)
  if (scan === undefined) return
  // Ctrl+V：先让浏览器触发 paste 事件，把本机剪贴板的文字发过去，再发按键
  if ((e.ctrlKey || e.metaKey) && e.code === 'KeyV' && !e.repeat) {
    pendingPaste = scan
    window.clearTimeout(pasteTimer)
    pasteTimer = window.setTimeout(flushPaste, 150)
    return
  }
  e.preventDefault()
  client?.send(encode.key(scan, true, modifiersOf(e)))
  if (tapsOnly(e)) client?.send(encode.key(scan, false, modifiersOf(e)))
}

function onKeyUp(e: KeyboardEvent): void {
  if (!control.value) return
  const scan = scanOf(e)
  if (scan === undefined) return
  if (pendingPaste === scan) flushPaste()
  e.preventDefault()
  client?.send(encode.key(scan, false, modifiersOf(e)))
}

function onPaste(e: ClipboardEvent): void {
  if (!control.value || !client || document.activeElement !== stage.value) return
  e.preventDefault()
  const text = e.clipboardData?.getData('text/plain')
  if (text) {
    if (utf8Length(text) > MAX_TEXT_BYTES) toast('warning', t('remote.viewer.textTruncated'))
    client.send(encode.clipboard(text))
  }
  flushPaste()
}

function flushPaste(): void {
  window.clearTimeout(pasteTimer)
  if (pendingPaste === null) return
  client?.send(encode.key(pendingPaste, true, 2))
  if (isMac) client?.send(encode.key(pendingPaste, false, 2))
  pendingPaste = null
}

/** 失焦、切到后台：松开所有键和鼠标键（不然对方电脑上会卡着一个 Ctrl） */
function releaseAll(): void {
  pendingPaste = null
  sentButtons.clear()
  if (control.value) client?.send(encode.releaseAll())
}

watch(textOpen, (open) => {
  if (open) void nextTick(() => textInput.value?.focus())
  else stage.value?.focus()
})
</script>

<template>
  <div ref="root" class="fixed inset-0 z-[2000] flex flex-col bg-black text-white" data-testid="remote-view">
    <!-- 工具栏 -->
    <header class="flex shrink-0 flex-wrap items-center gap-2 px-3 py-2" style="background: #1c1a19">
      <div class="min-w-0 flex-1 truncate text-sm">
        <span class="font-medium">{{ session?.deviceName }}</span>
        <span v-if="session && !control" class="ml-2 rounded px-1.5 py-0.5 text-xs" style="background: #3a3530">{{ t('remote.viewer.viewOnly') }}</span>
        <span v-if="statsText" class="ml-2 text-xs text-gray-400" data-testid="remote-stats">{{ statsText }}</span>
      </div>
      <template v-if="state === 'Active' && !replaced">
        <el-radio-group :model-value="quality" size="small" @update:model-value="(q) => setQuality(q as QualityPreset)">
          <el-radio-button :value="Quality.Smooth" data-testid="remote-quality-smooth">{{ t('remote.viewer.smooth') }}</el-radio-button>
          <el-radio-button :value="Quality.Sharp" data-testid="remote-quality-sharp">{{ t('remote.viewer.sharp') }}</el-radio-button>
        </el-radio-group>
        <!-- 全屏时弹层要留在全屏元素里面，不能挂到 body 上 -->
        <el-select v-if="monitors.length > 1" :model-value="video?.monitor" size="small" class="!w-36" :teleported="false" data-testid="remote-monitor" @update:model-value="selectMonitor">
          <el-option v-for="m in monitors" :key="m.index" :value="m.index" :label="`${t('remote.viewer.monitor', { n: m.index + 1 })} ${m.width}×${m.height}`" />
        </el-select>
        <template v-if="control">
          <el-dropdown trigger="click" :teleported="false" @command="special">
            <el-button size="small">{{ t('remote.viewer.keys') }}<el-icon class="ml-1"><ArrowDown /></el-icon></el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="win">{{ t('remote.viewer.win') }}</el-dropdown-item>
                <el-dropdown-item command="taskManager">{{ t('remote.viewer.taskManager') }}</el-dropdown-item>
                <el-dropdown-item command="altTab">{{ t('remote.viewer.altTab') }}</el-dropdown-item>
                <el-dropdown-item command="printScreen">{{ t('remote.viewer.printScreen') }}</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
          <el-button size="small" :icon="EditPen" data-testid="remote-type" @click="textOpen = !textOpen">{{ t('remote.viewer.typeText') }}</el-button>
        </template>
        <el-button size="small" :icon="FullScreen" @click="toggleFullscreen">{{ fullscreen ? t('remote.viewer.exitFullscreen') : t('remote.viewer.fullscreen') }}</el-button>
      </template>
      <el-button v-if="session && !session.endedAt" size="small" type="danger" :icon="Close" data-testid="remote-end" @click="end">{{ t('remote.viewer.end') }}</el-button>
    </header>

    <div v-if="textOpen && state === 'Active' && !replaced" class="flex shrink-0 gap-2 px-3 py-2" style="background: #26231f">
      <el-input ref="textInput" v-model="textValue" :placeholder="t('remote.viewer.typePlaceholder')" data-testid="remote-text" @keydown.enter="sendText" />
      <el-button type="primary" data-testid="remote-text-send" @click="sendText()">{{ t('remote.viewer.send') }}</el-button>
    </div>

    <!-- 画面 -->
    <div
      ref="stage"
      class="relative min-h-0 flex-1 outline-none"
      tabindex="0"
      data-testid="remote-stage"
      @keydown="onKeyDown"
      @keyup="onKeyUp"
      @focusout="releaseAll"
    >
      <canvas
        v-show="state === 'Active' && !replaced"
        ref="canvas"
        class="h-full w-full"
        :style="{ objectFit: 'contain', touchAction: control ? 'none' : 'auto' }"
        data-testid="remote-canvas"
        @pointermove="onPointerMove"
        @pointerdown="onPointerDown"
        @pointerup="onPointerUp"
        @pointercancel="onPointerUp"
        @mouseup="onMouseUp"
        @auxclick.prevent
        @wheel="onWheel"
        @contextmenu.prevent
      />

      <div v-if="notFound" class="absolute inset-0 flex flex-col items-center justify-center gap-4 p-6 text-center">
        <div>{{ t('remote.viewer.ended') }}</div>
        <el-button @click="router.push('/devices')">{{ t('remote.viewer.back') }}</el-button>
      </div>
      <div v-else-if="state === 'Pending'" class="absolute inset-0 flex flex-col items-center justify-center gap-3 p-6 text-center" data-testid="remote-pending">
        <div class="text-lg">{{ t('remote.viewer.waiting') }}</div>
        <div class="text-sm text-gray-400">{{ ownerName }} · {{ t('remote.viewer.secondsLeft', { n: secondsLeft }) }}</div>
        <el-button round @click="end">{{ t('common.cancel') }}</el-button>
      </div>
      <div v-else-if="state === 'Ended'" class="absolute inset-0 flex flex-col items-center justify-center gap-4 p-6 text-center" data-testid="remote-ended">
        <div class="text-lg" data-testid="remote-end-reason">{{ endReason }}</div>
        <div class="flex gap-2">
          <el-button round @click="router.push('/devices')">{{ t('remote.viewer.back') }}</el-button>
          <el-button round type="primary" @click="again">{{ t('remote.viewer.again') }}</el-button>
        </div>
      </div>
      <div v-else-if="state === 'Active' && replaced" class="absolute inset-0 flex flex-col items-center justify-center gap-4 p-6 text-center" data-testid="remote-replaced">
        <div class="text-lg">{{ t('remote.viewer.replaced') }}</div>
        <el-button round type="primary" data-testid="remote-take-over" @click="takeOver">{{ t('remote.viewer.takeOver') }}</el-button>
      </div>
      <template v-else-if="state === 'Active'">
        <div v-if="unsupported" class="absolute inset-0 flex items-center justify-center p-6 text-center">{{ t('remote.viewer.unsupported') }}</div>
        <div v-else-if="blocked" class="absolute inset-x-0 top-0 p-3 text-center text-sm" style="background: rgba(217, 48, 37, 0.85)" data-testid="remote-blocked">
          {{ blocked.code === StatusCode.SecureDesktop ? t('remote.viewer.secureDesktop') : t('remote.viewer.captureFailed', { msg: blocked.message }) }}
        </div>
        <div v-else-if="link !== 'streaming' || !video" class="pointer-events-none absolute inset-0 flex items-center justify-center text-sm text-gray-300" data-testid="remote-link">
          {{ link === 'reconnecting' ? t('remote.viewer.reconnecting') : link === 'waiting-agent' ? t('remote.viewer.waitingAgent') : t('remote.viewer.connecting') }}
        </div>
      </template>
    </div>
  </div>
</template>
