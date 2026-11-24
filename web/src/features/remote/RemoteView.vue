<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ArrowDown, Close, EditPen, FullScreen, Headset } from '@element-plus/icons-vue'
import { useFamilyStore } from '@/stores/family'
import { useRemoteStore } from '@/stores/remote'
import { RemoteClient, webCodecsSupported, type AudioState, type LineProblem, type LineState, type LinkState, type RemoteStats } from './RemoteClient'
import { realtimeSupported } from './RemoteRealtime'
import { audioSupported } from './RemoteAudio'
import {
  CUSTOM_FPS, CUSTOM_KBPS, CUSTOM_RESOLUTIONS, encode, Features, legacyQualityOf, MAX_TEXT_BYTES, modifiersOf, normalizePoint, normalizeVideoSettings, Quality, scanCodeOf, SpecialKey,
  StatsFlags, StatusCode, utf8Length, VIDEO_MODE_KEYS, VIDEO_PRESETS, VideoMode, WheelAccumulator, type MonitorInfo, type QualityPreset, type VideoSettings,
} from './protocol'
import { formatKbps } from './format'
import { ViewerTelemetry, viewerEnvironment } from './viewerTelemetry'
import { requestedSessionRoute, requestTimeInDocument } from './viewerStartup'
import { telemetryApi } from './telemetryApi'

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
/**
 * 画质：记在这台设备上（现在用哪一档、自定义里选过什么），默认自动。
 * 对方的程序是老版本（0.8.5 及以前，VideoConfig 里没有 VideoSettings 功能位）时只认识流畅 / 清晰两档：工具栏上给它原来的两个按钮（quality）
 */
const VIDEO_KEY = 'remote.video'
const savedVideo = loadVideo()
const videoSettings = ref<VideoSettings>(savedVideo.current)
const customVideo = ref<VideoSettings>(savedVideo.custom)
/** 自定义那一行（分辨率、帧率、码率上限）开着 */
const customOpen = ref(false)
const quality = ref<QualityPreset>(Quality.Smooth)
/** 对方的远程协助程序支持哪些可选功能（声音、码率自动调）；老版本是 0 */
const features = ref(0)
/** 要不要听对方电脑的声音：记在这台设备上，默认开 */
const SOUND_KEY = 'remote.sound'
const sound = ref(loadSound())
const audioState = ref<AudioState>('off')
/**
 * 画面和声音走哪条线路：快速（经 Cloudflare Realtime，不绕回家里的服务器）还是标准（经服务器中继）。
 * 记在这台设备上，默认快速。两条线路之间不自动换：快速线路出了问题只提示（lineProblem），要不要切到标准线路由用户决定。
 */
const LINE_KEY = 'remote.transport'
const fast = ref(loadFast())
const line = ref<LineState>('relay')
const lineProblem = ref<LineProblem | null>(null)
/** 已经排上去播放的声音（采样点数、最近的峰值）：端到端测试靠它确认真的解出了声音 */
const audioInfo = ref({ scheduled: 0, peak: 0 })
const fullscreen = ref(false)
const textOpen = ref(false)
const textValue = ref('')
const now = ref(Date.now())
let client: RemoteClient | null = null
let tick: number | undefined

/**
 * 这次远程里控制端这边的遥测（看到的帧率、卡住几次……，docs/DESIGN.md"远程协助遥测"）：跨重连、接手留着，
 * 每 60 秒报一次，结束、离开页面时报最后那份（final），关页面、刷新时会话还在就再报一份中途的（服务器按会话覆盖）。
 * 刷新页面接着同一次远程时，关页面前存到 sessionStorage 里、回来接着记。
 * 被别的窗口 / 设备接手了就不再报（那边报它自己看到的）。服务器明确不收（老版本、过了时间）就这次远程不再报。
 */
const TELEMETRY_EVERY_MS = 60_000
const TELEMETRY_SAVED_KEY = 'remote.telemetry.'
let telemetry: { sessionId: string; data: ViewerTelemetry; off: boolean; sentRevision: number; sentFinal: boolean } | null = null
let telemetryTimer: number | undefined

function telemetryFor(id: string): ViewerTelemetry {
  if (telemetry?.sessionId === id) return telemetry.data
  reportTelemetry(true)
  let data: ViewerTelemetry | null = null
  try {
    const saved = sessionStorage.getItem(TELEMETRY_SAVED_KEY + id)
    sessionStorage.removeItem(TELEMETRY_SAVED_KEY + id)
    if (saved) data = ViewerTelemetry.restore(JSON.parse(saved))
  } catch {
    // 无痕窗口等存不了、存的东西坏了：从头记
  }
  telemetry = { sessionId: id, data: data ?? new ViewerTelemetry(), off: false, sentRevision: -1, sentFinal: false }
  telemetry.data.startup.requested(requestTimeInDocument(window.history.state, id, performance.timeOrigin))
  return telemetry.data
}

/**
 * 报一份汇总。final：这次远程在这边结束了（结束、离开、换成下一次远程）的最后那份，已经报过、之后也没变就不再报。
 * beacon：用 sendBeacon（最后那份、关页面 / 刷新时那份），页面没了也送得出去；关页面时那份没有新东西也报（时长还在涨）。
 * 别的中途那份没有新东西就不报
 */
function reportTelemetry(final: boolean, beacon = final): void {
  const tel = telemetry
  if (!tel || tel.off || !tel.data.started) return
  if (tel.data.revision === tel.sentRevision && (final ? tel.sentFinal : !beacon)) return
  tel.sentRevision = tel.data.revision
  tel.sentFinal = final
  const body = tel.data.snapshot(performance.now(), viewerEnvironment(), final)
  void telemetryApi.viewer(tel.sessionId, body, beacon).then((ok) => {
    if (!ok) tel.off = true
  })
}

/**
 * 关页面（或刷新）：会话还没结束的话只报一份中途的（刷新回来接着记，最后那份等结束时再报），再存下来；已经结束了才报最后那份。
 * 会话还在时不报 final：服务器收到 final 以后不再收秒数没它多的中途那份（刷新回来没读到存的数、从头记的话就全丢了）
 */
function onPageHide(): void {
  if (replaced.value) return
  const ended = !session.value || !!session.value.endedAt
  reportTelemetry(ended, true)
  const tel = telemetry
  if (!tel || ended) return
  try {
    sessionStorage.setItem(TELEMETRY_SAVED_KEY + tel.sessionId, JSON.stringify(tel.data.save(performance.now())))
  } catch {
    // 存不了：刷新回来从头记
  }
}

const session = computed(() => remote.sessions[props.id])
const state = computed(() => session.value?.state)
const secureDesktop = computed(() => blocked.value?.code === StatusCode.SecureDesktop)
const control = computed(() => session.value?.mode === 'Control' && state.value === 'Active' && !replaced.value && !secureDesktop.value)
const secondsLeft = computed(() => (session.value ? Math.max(0, Math.ceil((new Date(session.value.expiresAt).getTime() - now.value) / 1000)) : 0))
const ownerName = computed(() => (session.value ? family.nameOf(session.value.deviceOwnerId) : ''))
const isMac = /Mac|iPhone|iPad/.test(navigator.platform)
const endReason = computed(() => {
  const r = session.value?.endReason
  return r && ['declined', 'timeout', 'controller', 'agent', 'stopped', 'revoked', 'disconnected', 'replaced'].includes(r) ? t(`remote.reasons.${r}`) : t('remote.viewer.ended')
})
/** 对方的程序能传声音，这个浏览器也放得了 */
const canSound = computed(() => (features.value & Features.Audio) !== 0 && audioSupported() && audioState.value !== 'unsupported')
const soundLabel = computed(() => {
  if (!canSound.value || !sound.value) return t('remote.viewer.soundOff')
  return audioState.value === 'blocked' ? t('remote.viewer.soundBlocked') : t('remote.viewer.soundOn')
})
const soundTitle = computed(() => {
  if (!video.value) return ''
  if ((features.value & Features.Audio) === 0) return t('remote.viewer.soundNeedsUpdate')
  if (!audioSupported() || audioState.value === 'unsupported') return t('remote.viewer.soundUnsupported')
  return ''
})
/** 对方的程序和这个浏览器都能走快速线路（服务器没配 Cloudflare Realtime 时对方不会声明支持） */
const canFast = computed(() => (features.value & Features.Realtime) !== 0 && realtimeSupported())
/**
 * 告诉用户画面糊是因为网络：自动画质因为网速降了分辨率，或者码率被压到了档位上限的四成以下而且不到 2 Mbps
 * （上限高的档位，5 Mbps 也够看清楚了，不算慢）
 */
const slow = computed(() => {
  const s = stats.value
  if (!s) return false
  return (s.flags & StatsFlags.LoweredForNetwork) !== 0 || (s.target > 0 && s.max > 0 && s.target < s.max * 0.4 && s.target < 2000)
})
/** 对方电脑采集、编码跟不上（软件编码，电脑慢的话分辨率高、帧率高就忙不过来）：告诉用户卡是因为那边的电脑 */
const busy = computed(() => !!stats.value && (stats.value.flags & (StatsFlags.CpuBound | StatsFlags.LoweredForCpu)) !== 0)
const busyText = computed(() => t((stats.value?.flags ?? 0) & StatsFlags.LoweredForCpu ? 'remote.viewer.busyLowered' : 'remote.viewer.busy'))
const busyTitle = computed(() => {
  const s = stats.value
  if (!s) return t('remote.viewer.busyTitleShort')
  // 只是之前忙不过来、已经降了分辨率（这一会儿跟得上）：不报现在每帧花多久——降了以后变少了，报出来像是不忙
  if ((s.flags & StatsFlags.CpuBound) === 0 && (s.flags & StatsFlags.LoweredForCpu) !== 0) return t('remote.viewer.busyLoweredTitle')
  const total = s.captureMs + s.encodeMs
  if (total <= 0) return t('remote.viewer.busyTitleShort')
  // 对方按每秒几帧出：设的帧率；这边请它少发过的话按请的（和它判断忙不忙用的是同一个数）
  const configured = video.value?.fps || 30
  const fps = s.fpsCap > 0 ? Math.min(s.fpsCap, configured) : configured
  const most = Math.floor(1000 / total)
  const cost = { ms: ms(total), capture: ms(s.captureMs), encode: ms(s.encodeMs) }
  // 按每帧花的时间算，每秒出不到这么多帧：说最多出几帧。还出得到（一帧的时间用掉八成五就算忙不过来）就说一帧的时间快用完了，
  // 不报一个比现在的帧率还高的数
  if (most < fps) return t('remote.viewer.busyTitle', { ...cost, fps: Math.max(1, most) })
  return t('remote.viewer.busyTitleTight', { ...cost, fps, interval: Math.round(1000 / fps) })
})
const oldAgent = computed(() => !!video.value && (features.value & Features.AdaptiveBitrate) === 0)
const statsText = computed(() => {
  if (!video.value || !stats.value) return ''
  const text = `${video.value.width}×${video.value.height} · ${stats.value.decoded} fps · ${formatKbps(stats.value.kbps)} · ${stats.value.rtt} ms`
  return line.value === 'realtime' ? `${text} · ${t('remote.viewer.lineFastActive')}` : text
})
/** 鼠标停在统计上时：对方电脑每帧采集、编码花多久（0.8.6 起），码率现在定在多少、最高多少 */
const statsTitle = computed(() => {
  const s = stats.value
  if (!s) return undefined
  const rate = { target: formatKbps(s.target), max: formatKbps(s.max) }
  if (s.captureMs > 0 || s.encodeMs > 0) return t('remote.viewer.statsTitle', { capture: ms(s.captureMs), encode: ms(s.encodeMs), ...rate })
  return s.max > 0 ? t('remote.viewer.statsTitleRate', rate) : undefined
})
/** 老版本的被控端只认识流畅 / 清晰两档：给它显示原来的两个按钮。还没收到 VideoConfig 时按新版本算 */
const legacyQuality = computed(() => !!video.value && (features.value & Features.VideoSettings) === 0)
const videoKey = computed(() => VIDEO_MODE_KEYS[videoSettings.value.mode])
const videoItems = computed(() => [
  { key: 'auto', name: t('remote.viewer.videoModes.auto'), detail: t('remote.viewer.videoAutoDetail') },
  ...(['smooth', 'clear', 'original'] as const).map((key) => ({ key, name: t(`remote.viewer.videoModes.${key}`), detail: videoDetail(VIDEO_PRESETS[key]) })),
  { key: 'custom', name: t('remote.viewer.videoCustomItem'), detail: videoDetail(customVideo.value) },
])

/** 毫秒数：保留一位小数，整数不带 .0 */
function ms(n: number): string {
  return String(Math.round(n * 10) / 10)
}

function resKey(w: number, h: number): string {
  return `${w}x${h}`
}

function videoDetail(s: VideoSettings): string {
  const res = s.maxWidth && s.maxHeight ? `${s.maxWidth}×${s.maxHeight}` : t('remote.viewer.videoNative')
  return t('remote.viewer.videoDetail', { res, fps: s.fps, kbps: formatKbps(s.maxKbps) })
}

function loadVideo(): { current: VideoSettings; custom: VideoSettings } {
  try {
    return normalizeVideoSettings(JSON.parse(localStorage.getItem(VIDEO_KEY) ?? 'null'))
  } catch {
    return normalizeVideoSettings(null)
  }
}

function loadFast(): boolean {
  try {
    return localStorage.getItem(LINE_KEY) !== 'relay'
  } catch {
    return true
  }
}

function loadSound(): boolean {
  try {
    return localStorage.getItem(SOUND_KEY) !== '0'
  } catch {
    return true
  }
}

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
  window.addEventListener('pagehide', onPageHide)
  telemetryTimer = window.setInterval(() => {
    if (!replaced.value) reportTelemetry(false)
  }, TELEMETRY_EVERY_MS)
  try {
    await remote.get(props.id)
  } catch {
    notFound.value = true
  }
})

onBeforeUnmount(() => {
  window.clearInterval(tick)
  window.clearInterval(telemetryTimer)
  document.removeEventListener('fullscreenchange', onFullscreenChange)
  document.removeEventListener('paste', onPaste)
  document.removeEventListener('visibilitychange', releaseAll)
  window.removeEventListener('blur', releaseAll)
  window.removeEventListener('pagehide', onPageHide)
  stopClient()
  if (!replaced.value) reportTelemetry(true)
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
  if (!replaced.value) reportTelemetry(true)
  telemetry = null
  notFound.value = false
  replaced.value = false
  link.value = 'connecting'
  video.value = null
  features.value = 0
  audioState.value = 'off'
  line.value = 'relay'
  lineProblem.value = null
  monitors.value = []
  stats.value = null
  blocked.value = null
  textOpen.value = false
  customOpen.value = false
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
    if (!replaced.value) reportTelemetry(true)
  }
}, { immediate: true })

function startClient(): void {
  if (!canvas.value) return
  audioState.value = 'off'
  audioInfo.value = { scheduled: 0, peak: 0 }
  line.value = 'relay'
  lineProblem.value = null
  const c = new RemoteClient(props.id, canvas.value, {
    state: (s) => (link.value = s),
    config: (v) => {
      video.value = v
      features.value = v.features
      // 知道对方能传声音了：按这台设备上记的选择打开（只在状态要变的时候说）
      if (sound.value && canSound.value && audioState.value === 'off') c.setAudio(true)
    },
    monitors: (m) => (monitors.value = m),
    stats: (s) => {
      stats.value = s
      audioInfo.value = c.audioProgress
    },
    status: (code, message) => {
      if (client !== c) return
      if (code === StatusCode.Unsupported) {
        toast('warning', message)
        return
      }
      if (code === StatusCode.SecureDesktop) {
        clearPendingInput()
        textOpen.value = false
        textValue.value = ''
      } else if (secureDesktop.value) {
        if (code !== StatusCode.Ok) return
        // 两条线路共用 canvas。恢复显示前清掉上次的画面，等新画面到达后再展示。
        const surface = canvas.value
        surface?.getContext('2d')?.clearRect(0, 0, surface.width, surface.height)
      }
      blocked.value = code === StatusCode.Ok ? null : { code, message }
    },
    clipboard: (text) => {
      if (!control.value || client !== c) return
      navigator.clipboard?.writeText(text).then(() => toast('success', t('remote.viewer.clipboardIn')), () => undefined)
    },
    ended: (why) => {
      if (client !== c) return
      client = null
      clearPendingInput()
      if (why === 'replaced') {
        // 在别的窗口 / 设备上接着用了：把这边看到的先报上去，之后由那边报
        reportTelemetry(false)
        replaced.value = true
      }
      void remote.get(props.id).catch(() => undefined)
    },
    unsupported: () => (unsupported.value = true),
    audio: (s) => (audioState.value = s),
    line: (s, problem) => {
      line.value = s
      // 快速线路接不上、中途断了：不自动换线路，把原因摆在画面上，由用户决定要不要切到标准线路
      lineProblem.value = problem ?? null
    },
  }, telemetryFor(props.id))
  client = c
  // 画质按这台设备上记着的来：先交给它记着，对方连上来就发（老版本的程序按最接近的那一档）
  quality.value = legacyQualityOf(videoSettings.value)
  c.setVideo(videoSettings.value)
  // 选的是快速线路的话，等对方的程序报上来支持就去接（只用这条线路）
  c.setTransport(fast.value)
  c.connect()
  void nextTick(() => stage.value?.focus())
}

function stopClient(): void {
  if (!client) return
  client.close()
  client = null
  clearPendingInput()
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
  const requestedAt = performance.now()
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
    await router.replace(requestedSessionRoute(next.id, requestedAt))
  } catch (e) {
    toast('error', (e as Error).message)
  }
}

/** 老版本的程序：两档里选一档。不记到这台设备上（记着的还是在新版本上选的），只记在这次远程里 */
function setQuality(q: QualityPreset): void {
  quality.value = q
  // 对方中途升级成新版本的话，菜单上显示的和它用着的一致（它会把这一档对到流畅 / 清晰）
  videoSettings.value = { ...(q === Quality.Sharp ? VIDEO_PRESETS.clear : VIDEO_PRESETS.smooth) }
  client?.setQuality(q)
}

function sameVideo(a: VideoSettings, b: VideoSettings): boolean {
  return a.mode === b.mode && a.maxWidth === b.maxWidth && a.maxHeight === b.maxHeight && a.fps === b.fps && a.maxKbps === b.maxKbps
}

/** 换画质：马上告诉对方，并记在这台设备上 */
function applyVideo(s: VideoSettings): void {
  const changed = !sameVideo(s, videoSettings.value)
  videoSettings.value = s
  // 老版本的两个按钮跟着这次发出去的 SetQuality 走：还没收到 VideoConfig 时在菜单里选过，对方原来是老版本的话，按钮上亮的就是它用着的那档
  quality.value = legacyQualityOf(s)
  try {
    localStorage.setItem(VIDEO_KEY, JSON.stringify({ current: s, custom: customVideo.value }))
  } catch {
    // 无痕窗口等存不了：这次会话里照样生效
  }
  if (changed) client?.setVideo(s)
}

/** 画质菜单：几个固定档直接换；自定义…换成自定义里选的，并打开那一行 */
function pickVideo(command: string): void {
  if (command === 'custom') {
    applyVideo({ ...customVideo.value })
    customOpen.value = true
    // 焦点回到画面：不然菜单收起后焦点落在画质按钮上，接着按的键到不了对方电脑（方向键、回车还会把菜单再打开）
    stage.value?.focus()
    return
  }
  const preset = VIDEO_PRESETS[command as keyof typeof VIDEO_PRESETS]
  if (!preset) return
  applyVideo({ ...preset })
  customOpen.value = false
  stage.value?.focus()
}

/** 自定义那一行里改了一项：马上按自定义用上 */
function setCustom(patch: Partial<Omit<VideoSettings, 'mode'>>): void {
  customVideo.value = { ...customVideo.value, ...patch, mode: VideoMode.Custom }
  applyVideo({ ...customVideo.value })
  refocusAfterSelect()
}

/**
 * 下拉框里选完一项，焦点回到画面。el-select 发出选中的值之后才把焦点放回它自己的输入框，这里马上 focus 会被它盖掉，
 * 所以等下一拍。不然接着按的方向键、回车被下拉框接走：打开菜单、换成别的选项，键也到不了对方电脑
 */
function refocusAfterSelect(): void {
  void nextTick(() => stage.value?.focus())
}

function setCustomRes(key: string): void {
  const r = CUSTOM_RESOLUTIONS.find(([w, h]) => resKey(w, h) === key)
  if (r) setCustom({ maxWidth: r[0], maxHeight: r[1] })
}

function closeCustom(): void {
  customOpen.value = false
  stage.value?.focus()
}

function setFast(value: boolean): void {
  fast.value = value
  try {
    localStorage.setItem(LINE_KEY, value ? 'realtime' : 'relay')
  } catch {
    // 无痕窗口等存不了：这次会话里照样生效
  }
  client?.setTransport(value)
  stage.value?.focus()
}

/** 跳过自动重试的等待，或在尝试用尽后再试一轮 */
function retryFast(): void {
  client?.retryRealtime()
  stage.value?.focus()
}

function selectMonitor(index: number): void {
  client?.selectMonitor(index)
  refocusAfterSelect()
}

function toggleSound(): void {
  // 浏览器还不让出声（刷新页面后直接进来的）：这一下点击就是它要的"用户操作"，不是要关声音
  if (sound.value && audioState.value === 'blocked') {
    client?.resumeAudio()
    return
  }
  sound.value = !sound.value
  try {
    localStorage.setItem(SOUND_KEY, sound.value ? '1' : '0')
  } catch {
    // 无痕窗口等存不了：这次会话里照样生效
  }
  client?.setAudio(sound.value && canSound.value)
  client?.resumeAudio()
  stage.value?.focus()
}

function special(command: string): void {
  if (!control.value) return
  const id = { win: SpecialKey.Win, taskManager: SpecialKey.TaskManager, altTab: SpecialKey.AltTab, printScreen: SpecialKey.PrintScreen }[command]
  if (id) client?.send(encode.specialKey(id))
  stage.value?.focus()
}

function sendText(e?: Event): void {
  if (!control.value || (e as KeyboardEvent | undefined)?.isComposing) return
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
  if (pendingMove && control.value) client?.send(encode.mouseMove(pendingMove.x, pendingMove.y), true)
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
  client?.resumeAudio()
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
  client?.resumeAudio()
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
  if (pendingPaste === null || !control.value) return
  client?.send(encode.key(pendingPaste, true, 2))
  if (isMac) client?.send(encode.key(pendingPaste, false, 2))
  pendingPaste = null
}

function clearPendingInput(): void {
  if (moveFrame) cancelAnimationFrame(moveFrame)
  moveFrame = 0
  pendingMove = null
  window.clearTimeout(pasteTimer)
  pendingPaste = null
  sentButtons.clear()
}

/** 失焦、切到后台：松开所有键和鼠标键（不然对方电脑上会卡着一个 Ctrl） */
function releaseAll(): void {
  clearPendingInput()
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
        <span v-if="session?.mode === 'View'" class="ml-2 rounded px-1.5 py-0.5 text-xs" style="background: #3a3530">{{ t('remote.viewer.viewOnly') }}</span>
        <template v-if="!secureDesktop">
          <span
            v-if="statsText" class="ml-2 text-xs text-gray-400" :data-line="line" :title="statsTitle"
            :data-capture-ms="stats?.captureMs" :data-encode-ms="stats?.encodeMs" :data-flags="stats?.flags" data-testid="remote-stats"
          >{{ statsText }}</span>
          <span v-if="slow" class="ml-2 rounded px-1.5 py-0.5 text-xs" style="background: #5c4514; color: #ffd98a" :title="t('remote.viewer.slowTitle', { target: formatKbps(stats!.target), max: formatKbps(stats!.max) })" data-testid="remote-slow">{{ t('remote.viewer.slow') }}</span>
          <span v-else-if="oldAgent" class="ml-2 rounded px-1.5 py-0.5 text-xs" style="background: #3a3530" :title="t('remote.viewer.oldAgentTitle')" data-testid="remote-old-agent">{{ t('remote.viewer.oldAgent') }}</span>
          <span v-if="busy" class="ml-2 rounded px-1.5 py-0.5 text-xs" style="background: #5c4514; color: #ffd98a" :title="busyTitle" data-testid="remote-busy">{{ busyText }}</span>
        </template>
      </div>
      <template v-if="state === 'Active' && !replaced && !secureDesktop">
        <!-- 老版本的程序只认识两档 -->
        <el-radio-group v-if="legacyQuality" :model-value="quality" size="small" @update:model-value="(q) => setQuality(q as QualityPreset)">
          <el-radio-button :value="Quality.Smooth" data-testid="remote-quality-smooth">{{ t('remote.viewer.smooth') }}</el-radio-button>
          <el-radio-button :value="Quality.Sharp" data-testid="remote-quality-sharp">{{ t('remote.viewer.sharp') }}</el-radio-button>
        </el-radio-group>
        <el-dropdown v-else trigger="click" :teleported="false" @command="pickVideo">
          <el-button size="small" :title="t('remote.viewer.videoTitle')" data-testid="remote-video">{{ t('remote.viewer.video', { name: t(`remote.viewer.videoModes.${videoKey}`) }) }}<el-icon class="ml-1"><ArrowDown /></el-icon></el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item v-for="item in videoItems" :key="item.key" :command="item.key" :divided="item.key === 'custom'" :data-testid="`remote-video-${item.key}`">
                <div class="flex flex-col py-0.5 leading-tight">
                  <span :style="item.key === videoKey ? 'color: var(--el-color-primary); font-weight: 500' : undefined">{{ item.name }}</span>
                  <span class="text-xs" style="color: var(--el-text-color-secondary)">{{ item.detail }}</span>
                </div>
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <el-radio-group v-if="canFast" :model-value="fast" size="small" :title="t('remote.viewer.lineTitle')" @update:model-value="(v) => setFast(v as boolean)">
          <el-radio-button :value="true" data-testid="remote-line-fast">{{ line === 'connecting' ? t('remote.viewer.lineConnecting') : t('remote.viewer.lineFast') }}</el-radio-button>
          <el-radio-button :value="false" data-testid="remote-line-standard">{{ t('remote.viewer.lineStandard') }}</el-radio-button>
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
      </template>
      <el-button v-if="state === 'Active' && !replaced && video" size="small" :icon="Headset" :type="sound && canSound ? 'primary' : 'default'" :disabled="!canSound" :title="soundTitle" :data-audio="audioState" :data-scheduled="audioInfo.scheduled" :data-peak="audioInfo.peak.toFixed(3)" data-testid="remote-sound" @click="toggleSound">{{ soundLabel }}</el-button>
      <el-button v-if="state === 'Active' && !replaced && (!secureDesktop || fullscreen)" size="small" :icon="FullScreen" @click="toggleFullscreen">{{ fullscreen ? t('remote.viewer.exitFullscreen') : t('remote.viewer.fullscreen') }}</el-button>
      <el-button v-if="session && !session.endedAt" size="small" type="danger" :icon="Close" data-testid="remote-end" @click="end">{{ t('remote.viewer.end') }}</el-button>
    </header>

    <div v-if="textOpen && control" class="flex shrink-0 gap-2 px-3 py-2" style="background: #26231f">
      <el-input ref="textInput" v-model="textValue" :placeholder="t('remote.viewer.typePlaceholder')" data-testid="remote-text" @keydown.enter="sendText" />
      <el-button type="primary" data-testid="remote-text-send" @click="sendText()">{{ t('remote.viewer.send') }}</el-button>
    </div>

    <!-- 自定义画质：改一项马上用上 -->
    <div
      v-if="customOpen && state === 'Active' && !replaced && !secureDesktop && !legacyQuality"
      class="flex shrink-0 flex-wrap items-center gap-x-3 gap-y-2 px-3 py-2 text-xs" style="background: #26231f" data-testid="remote-video-custom-row"
    >
      <div class="flex items-center gap-1.5">
        <span class="text-gray-300">{{ t('remote.viewer.videoRes') }}</span>
        <el-select
          :model-value="resKey(customVideo.maxWidth, customVideo.maxHeight)" size="small" class="!w-28" :teleported="false" data-testid="remote-video-res"
          @update:model-value="(v) => setCustomRes(String(v))"
        >
          <el-option v-for="[w, h] in CUSTOM_RESOLUTIONS" :key="resKey(w, h)" :value="resKey(w, h)" :label="w && h ? `${w}×${h}` : t('remote.viewer.videoResNative')" />
        </el-select>
      </div>
      <div class="flex items-center gap-1.5">
        <span class="text-gray-300">{{ t('remote.viewer.videoFps') }}</span>
        <el-select :model-value="customVideo.fps" size="small" class="!w-20" :teleported="false" data-testid="remote-video-fps" @update:model-value="(v) => setCustom({ fps: Number(v) })">
          <el-option v-for="f in CUSTOM_FPS" :key="f" :value="f" :label="t('remote.viewer.videoFpsOption', { n: f })" />
        </el-select>
      </div>
      <div class="flex items-center gap-1.5">
        <span class="text-gray-300">{{ t('remote.viewer.videoKbps') }}</span>
        <el-select :model-value="customVideo.maxKbps" size="small" class="!w-24" :teleported="false" data-testid="remote-video-kbps" @update:model-value="(v) => setCustom({ maxKbps: Number(v) })">
          <el-option v-for="k in CUSTOM_KBPS" :key="k" :value="k" :label="`${k / 1000} Mbps`" />
        </el-select>
      </div>
      <span class="min-w-[12rem] flex-1 text-gray-400">{{ t('remote.viewer.videoCustomHint') }}</span>
      <el-button size="small" data-testid="remote-video-custom-close" @click="closeCustom">{{ t('remote.viewer.videoCustomClose') }}</el-button>
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
        v-show="state === 'Active' && !replaced && !secureDesktop"
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
        <div v-else-if="secureDesktop" class="absolute inset-0 flex flex-col items-center justify-center gap-2 p-6 text-center" role="status" data-testid="remote-blocked">
          <div class="text-lg">{{ t('remote.viewer.secureDesktop') }}</div>
          <div class="max-w-md text-sm text-gray-300">{{ t('remote.viewer.secureDesktopHint') }}</div>
        </div>
        <div v-else-if="blocked" class="absolute inset-x-0 top-0 p-3 text-center text-sm" style="background: rgba(217, 48, 37, 0.85)" data-testid="remote-blocked">
          {{ t('remote.viewer.captureFailed', { msg: blocked.message }) }}
        </div>
        <div v-else-if="link !== 'streaming' || !video" class="pointer-events-none absolute inset-0 flex items-center justify-center text-sm text-gray-300" data-testid="remote-link">
          {{ link === 'reconnecting' ? t('remote.viewer.reconnecting') : link === 'waiting-agent' ? t('remote.viewer.waitingAgent') : t('remote.viewer.connecting') }}
        </div>
        <!-- 选的是快速线路，但它还没接上 / 出了问题：这期间没有画面（不拿标准线路的顶上），在这里说明并给出手动切换的入口 -->
        <div v-else-if="line === 'connecting'" class="pointer-events-none absolute inset-0 flex flex-col items-center justify-center gap-3 p-6 text-center text-sm"
          style="background: rgba(0, 0, 0, 0.55)" data-testid="remote-line-wait">
          <div v-if="lineProblem" :data-retrying="lineProblem.retrying" data-testid="remote-line-problem">{{ t(lineProblem.retrying ? 'remote.viewer.lineProblemRetrying' : 'remote.viewer.lineProblem', { reason: lineProblem.reason }) }}</div>
          <div v-else class="text-gray-300">{{ t('remote.viewer.lineConnecting') }}</div>
          <div class="pointer-events-auto flex gap-2">
            <el-button v-if="lineProblem" round size="small" data-testid="remote-line-retry" @click="retryFast">{{ t('remote.viewer.lineRetry') }}</el-button>
            <el-button round size="small" type="primary" data-testid="remote-line-use-standard" @click="setFast(false)">{{ t('remote.viewer.lineUseStandard') }}</el-button>
          </div>
        </div>
      </template>
    </div>
  </div>
</template>
