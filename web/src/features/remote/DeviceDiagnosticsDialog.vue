<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useMediaQuery } from '@vueuse/core'
import type { DeviceDto } from '@/stores/remote'
import { telemetryApi } from './telemetryApi'
import { formatBytes, formatKbps, formatWhen } from './format'
import { VIDEO_MODE_KEYS } from './protocol'
import type {
  DeviceTelemetryDto, SessionTelemetryDto, TelemetryDisplay, TelemetryEncoder, TelemetryGpu, TelemetryOs, TelemetrySettingsUse, TelemetrySizeUse, TelemetryStat,
} from './telemetry'

/**
 * 管理员看一台电脑的遥测（docs/DESIGN.md"远程协助遥测"）：硬件和能力（被控端连上服务器后报的）、
 * 能用的编码器和截屏方式（下一步做硬件编码、Desktop Duplication 前先看每台电脑能做什么）、最近几次远程两边报上来的汇总。
 * "复制原始数据"把服务器给的整份 JSON 放进剪贴板，可以直接贴给开发的人看。
 */
const open = defineModel<boolean>({ required: true })
const props = defineProps<{ device: DeviceDto | null }>()

const { t } = useI18n()
/** 手机上整屏显示（表格在自己里面横着滚，页面不横着滚） */
const wide = useMediaQuery('(min-width: 640px)')

const data = ref<DeviceTelemetryDto | null>(null)
const loading = ref(false)
const error = ref('')
const rawText = ref('')
let seq = 0

const snap = computed(() => data.value?.snapshot ?? null)
const sessions = computed(() => data.value?.sessions ?? [])

watch(open, (v) => {
  if (!v) return
  if (data.value && data.value.deviceId !== props.device?.id) data.value = null
  rawText.value = ''
  void load()
}, { immediate: true })

async function load(): Promise<void> {
  const d = props.device
  if (!d) return
  const mine = ++seq
  loading.value = true
  error.value = ''
  try {
    const r = await telemetryApi.device(d.id)
    if (mine === seq) data.value = r
  } catch (e) {
    if (mine === seq) error.value = (e as Error).message
  } finally {
    if (mine === seq) loading.value = false
  }
}

async function copy(value: unknown): Promise<void> {
  const text = JSON.stringify(value, null, 2)
  try {
    await navigator.clipboard.writeText(text)
    ElMessage.success(t('remote.diagnostics.copied'))
  } catch {
    // 非安全上下文没有剪贴板接口：放进下面的框里让人自己全选复制
    rawText.value = text
    ElMessage.warning(t('remote.diagnostics.copyFailed'))
  }
}

// ---- 显示用的几个小函数 ----

/** el-table 的插槽里 row 没有类型：按这张表放进去的数据认回来 */
const asEncoder = (row: unknown): TelemetryEncoder => row as TelemetryEncoder
const asSession = (row: unknown): SessionTelemetryDto => row as SessionTelemetryDto

const VENDORS: Record<number, string> = { 0x10de: 'NVIDIA', 0x1002: 'AMD', 0x1022: 'AMD', 0x8086: 'Intel', 0x1414: 'Microsoft', 0x5143: 'Qualcomm' }

function vendorName(id: number): string {
  if (!id) return '—'
  return VENDORS[id] ?? `0x${id.toString(16).toUpperCase().padStart(4, '0')}`
}

/** 毫秒数等：保留一位小数，整数不带 .0；没有是 — */
function num(n: number | null | undefined): string {
  return typeof n === 'number' && Number.isFinite(n) ? String(Math.round(n * 10) / 10) : '—'
}

/** 统计里的两个分位："P50 / P10" 这种；没有样本是 — */
function pair(s: TelemetryStat | null | undefined, a: 'p50', b: 'p10' | 'p90'): string {
  return s && s.samples ? `${num(s[a])} / ${num(s[b])}` : '—'
}

const mark = (b: boolean | undefined): string => (b ? '✓' : '—')
const yesNo = (b: boolean): string => t(b ? 'remote.diagnostics.yes' : 'remote.diagnostics.no')

function osText(os: TelemetryOs): string {
  // Windows 11 的注册表里 ProductName 还写着 Windows 10：按版本号认
  const name = os.build >= 22000 ? os.productName.replace(/^Windows 10/, 'Windows 11') : os.productName
  return `${name} ${os.displayVersion}（${os.build}.${os.revision}）· ${t(os.is64Bit ? 'remote.diagnostics.bits64' : 'remote.diagnostics.bits32')}`
}

const cpuText = computed(() => {
  const c = snap.value?.cpu
  return c ? t('remote.diagnostics.cpuValue', { name: c.name || '—', cores: c.cores, threads: c.threads, mhz: c.baseMhz }) : '—'
})

function gpuText(g: TelemetryGpu): string {
  const parts = [g.name || '—', vendorName(g.vendorId), t('remote.diagnostics.gpuDriver', { v: g.driverVersion || '—' })]
  if (g.dedicatedMemoryBytes > 0) parts.push(t('remote.diagnostics.gpuMemory', { size: formatBytes(g.dedicatedMemoryBytes) }))
  if (g.software) parts.push(t('remote.diagnostics.gpuSoftware'))
  if (g.outputs?.length) parts.push(g.outputs.join('、'))
  return parts.join(' · ')
}

function displayText(d: TelemetryDisplay): string {
  const parts = [d.deviceName, `${d.width}×${d.height}`, `${d.refreshHz} Hz`, t('remote.diagnostics.displayScale', { n: d.dpiScalePercent })]
  if (d.primary) parts.push(t('remote.diagnostics.displayPrimary'))
  if (d.adapter) parts.push(d.adapter)
  return parts.join(' · ')
}

function testText(e: TelemetryEncoder): string {
  const r = e.test
  if (!r || r.framesOut <= 0) return '—'
  return t('remote.diagnostics.testValue', { avg: num(r.avgMs), p90: num(r.p90Ms), first: num(r.firstFrameMs), out: r.framesOut, frames: r.frames, size: formatBytes(r.bytes) })
}

function encoderError(e: TelemetryEncoder): string {
  const list = [e.error, e.test?.error].filter((x): x is string => !!x)
  if (!list.length && !e.activated) list.push(t('remote.diagnostics.notActivated'))
  return list.join('；')
}

const gdiText = computed(() => {
  const c = snap.value?.capture
  if (!c || (c.gdiNativeMs === null && c.gdiScaledMs === null)) return ''
  return t('remote.diagnostics.gdi', { native: num(c.gdiNativeMs), scaled: num(c.gdiScaledMs) })
})

function durationText(seconds: number): string {
  const s = Math.max(0, Math.round(seconds))
  if (s < 60) return t('remote.diagnostics.seconds', { n: s })
  if (s < 3600) return t('remote.diagnostics.minutes', { m: Math.floor(s / 60), s: s % 60 })
  return t('remote.diagnostics.hours', { h: Math.floor(s / 3600), m: Math.floor((s % 3600) / 60) })
}

function sessionDuration(s: SessionTelemetryDto): string {
  if (!s.endedAt) return t('remote.diagnostics.ongoing')
  return durationText((new Date(s.endedAt).getTime() - new Date(s.startedAt).getTime()) / 1000)
}

function lineText(s: SessionTelemetryDto): string {
  const src = s.agent ?? s.viewer
  if (!src) return '—'
  const parts: string[] = []
  if (src.relaySeconds > 0) parts.push(t('remote.diagnostics.lineRelay', { t: durationText(src.relaySeconds) }))
  if (src.realtimeSeconds > 0) parts.push(t('remote.diagnostics.lineRealtime', { t: durationText(src.realtimeSeconds) }))
  return parts.join(' · ') || '—'
}

/** 画质档位的名字（服务器给的是字符串；老数据万一是数字也认） */
function modeName(mode: unknown): string {
  const key = typeof mode === 'number' ? VIDEO_MODE_KEYS[mode] : typeof mode === 'string' ? mode.toLowerCase() : undefined
  return key && (VIDEO_MODE_KEYS as readonly string[]).includes(key) ? t(`remote.viewer.videoModes.${key}`) : String(mode ?? '—')
}

function settingsText(list: TelemetrySettingsUse[] | undefined): string {
  if (!list?.length) return '—'
  return [...list].sort((a, b) => b.seconds - a.seconds).map((u) => {
    const res = u.maxWidth && u.maxHeight ? `${u.maxWidth}×${u.maxHeight}` : t('remote.diagnostics.native')
    return `${modeName(u.mode)} ${res} ${t('remote.viewer.videoFpsOption', { n: u.fps })} ${formatKbps(u.maxKbps)}（${durationText(u.seconds)}）`
  }).join('；')
}

function sizesText(list: TelemetrySizeUse[] | undefined): string {
  if (!list?.length) return '—'
  return [...list].sort((a, b) => b.seconds - a.seconds).map((u) => `${u.width}×${u.height}（${durationText(u.seconds)}）`).join('；')
}

function freezeText(s: SessionTelemetryDto): string {
  return s.viewer ? t('remote.diagnostics.freezeValue', { n: s.viewer.freezes, ms: s.viewer.longestFreezeMs }) : '—'
}

function autoText(s: SessionTelemetryDto): string {
  return s.agent ? `${s.agent.autoNetworkSteps} / ${s.agent.autoCpuSteps} / ${s.agent.autoCpuUndos}` : '—'
}

function errorsOf(s: SessionTelemetryDto): string[] {
  return [...(s.agent?.errors ?? []), ...(s.viewer?.errors ?? []), ...(s.agent?.lineProblems ?? []), ...(s.viewer?.lineProblems ?? [])]
}

function isStat(v: unknown): v is TelemetryStat {
  return !!v && typeof v === 'object' && 'samples' in v && 'p50' in v
}

/** 展开的那一行：一边的汇总里所有字段，原样的字段名 + 好读一点的值 */
function valueText(v: unknown): string {
  if (v === null || v === undefined || v === '') return '—'
  if (typeof v === 'boolean') return yesNo(v)
  if (isStat(v)) {
    if (!v.samples) return '—'
    return t('remote.diagnostics.stat', { n: v.samples, mean: num(v.mean), min: num(v.min), p10: num(v.p10), p50: num(v.p50), p90: num(v.p90), max: num(v.max) })
  }
  if (Array.isArray(v)) return v.length ? v.map(valueText).join('；') : '—'
  if (typeof v === 'object') return Object.entries(v).map(([k, x]) => `${k}=${valueText(x)}`).join(' ')
  return String(v)
}

function sides(s: SessionTelemetryDto): { key: string; label: string; empty: string; fields: { key: string; value: string }[] }[] {
  const fields = (o: object | null) => (o ? Object.entries(o).map(([key, v]) => ({ key, value: valueText(v) })) : [])
  return [
    { key: 'agent', label: t('remote.diagnostics.agentSide'), empty: t('remote.diagnostics.noAgentData'), fields: fields(s.agent) },
    { key: 'viewer', label: t('remote.diagnostics.viewerSide'), empty: t('remote.diagnostics.noViewerData'), fields: fields(s.viewer) },
  ]
}
</script>

<template>
  <el-dialog
    v-model="open" :title="t('remote.diagnostics.title', { name: data?.deviceName ?? device?.name ?? '' })" :width="wide ? 'min(1040px, 96vw)' : '100%'" :fullscreen="!wide"
    append-to-body data-testid="diagnostics-dialog"
  >
    <div v-loading="loading && !data" class="flex min-h-24 flex-col gap-5" :data-loaded="data ? 'true' : 'false'">
      <el-alert v-if="error" type="error" :closable="false" :title="t('remote.diagnostics.loadFailed', { msg: error })" />

      <template v-if="data">
        <p class="text-xs leading-relaxed" style="color: var(--pc-text-muted)">{{ t('remote.diagnostics.privacy') }}</p>

        <!-- 这台电脑 -->
        <section data-testid="diagnostics-hardware">
          <h3 class="mb-2 text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('remote.diagnostics.hardware') }}</h3>
          <div v-if="!snap" class="text-sm" style="color: var(--pc-text-muted)" data-testid="diagnostics-no-snapshot">{{ t('remote.diagnostics.noSnapshot') }}</div>
          <el-descriptions v-else :column="wide ? 2 : 1" border size="small">
            <el-descriptions-item :label="t('remote.diagnostics.os')">{{ osText(snap.os) }}</el-descriptions-item>
            <el-descriptions-item :label="t('remote.diagnostics.cpu')">
              <span data-testid="diagnostics-cpu" :data-name="snap.cpu.name">{{ cpuText }}</span>
            </el-descriptions-item>
            <el-descriptions-item :label="t('remote.diagnostics.memory')">{{ snap.memoryBytes > 0 ? formatBytes(snap.memoryBytes) : '—' }}</el-descriptions-item>
            <el-descriptions-item :label="t('remote.diagnostics.rdp')">{{ yesNo(snap.remoteDesktopSession) }}</el-descriptions-item>
            <el-descriptions-item :label="t('remote.diagnostics.gpus')" :span="wide ? 2 : 1">
              <div v-for="(g, i) in snap.gpus" :key="i" class="break-words" data-testid="diagnostics-gpu" :data-name="g.name">{{ gpuText(g) }}</div>
              <span v-if="!snap.gpus.length">—</span>
            </el-descriptions-item>
            <el-descriptions-item :label="t('remote.diagnostics.displays')" :span="wide ? 2 : 1">
              <div v-for="(d, i) in snap.displays" :key="i" class="break-words" data-testid="diagnostics-display">{{ displayText(d) }}</div>
              <span v-if="!snap.displays.length">—</span>
            </el-descriptions-item>
            <el-descriptions-item :label="t('remote.diagnostics.agentVersion')">v{{ snap.agentVersion }}</el-descriptions-item>
            <el-descriptions-item :label="t('remote.diagnostics.collectedAt')">
              {{ formatWhen(snap.collectedAt) }}<template v-if="data.reportedAt"> · {{ t('remote.diagnostics.reportedAt') }} {{ formatWhen(data.reportedAt) }}</template>
            </el-descriptions-item>
            <el-descriptions-item :label="t('remote.diagnostics.problems')" :span="wide ? 2 : 1">
              <div v-for="(p, i) in snap.problems" :key="i" class="break-words" style="color: #e5484d" data-testid="diagnostics-problem">{{ p }}</div>
              <span v-if="!snap.problems.length">{{ t('remote.diagnostics.none') }}</span>
            </el-descriptions-item>
          </el-descriptions>
        </section>

        <!-- 编码和截屏 -->
        <section v-if="snap" data-testid="diagnostics-encoders">
          <h3 class="mb-2 text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('remote.diagnostics.encoding') }}</h3>
          <div v-if="!snap.encoders.length" class="text-sm" style="color: var(--pc-text-muted)">{{ t('remote.diagnostics.noEncoders') }}</div>
          <el-table v-else :data="snap.encoders" size="small" border class="w-full">
            <el-table-column :label="t('remote.diagnostics.encoder')" min-width="210">
              <template #default="{ row }">
                <span
                  data-testid="diagnostics-encoder" :data-name="row.name" :data-hardware="String(row.hardware)"
                  :data-test-avg-ms="row.test?.avgMs ?? ''" :data-test-frames-out="row.test?.framesOut ?? ''"
                >{{ row.name }}</span>
                <el-tag size="small" round class="ml-1" :type="row.hardware ? 'success' : 'info'">
                  {{ row.hardware ? t('remote.diagnostics.encoderHardware') : t('remote.diagnostics.encoderSoftware') }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.vendor')" min-width="90">
              <template #default="{ row }">{{ vendorName(row.vendorId) }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.qualityMode')" min-width="72" align="center">
              <template #default="{ row }">{{ mark(row.qualityMode) }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.perFrameQp')" min-width="72" align="center">
              <template #default="{ row }">{{ mark(row.perFrameQp) }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.lowLatency')" min-width="72" align="center">
              <template #default="{ row }">{{ mark(row.lowLatency) }}</template>
            </el-table-column>
            <el-table-column label="1080p" min-width="64" align="center">
              <template #default="{ row }">{{ mark(row.accepts1080p) }}</template>
            </el-table-column>
            <el-table-column label="4K" min-width="56" align="center">
              <template #default="{ row }">{{ mark(row.accepts4k) }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.rateControl')" min-width="150">
              <template #default="{ row }">{{ row.rateControlModes?.length ? row.rateControlModes.join(' / ') : '—' }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.test')" min-width="280">
              <template #default="{ row }">{{ testText(asEncoder(row)) }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.error')" min-width="180">
              <template #default="{ row }"><span style="color: #e5484d">{{ encoderError(asEncoder(row)) }}</span></template>
            </el-table-column>
          </el-table>

          <div v-if="gdiText" class="mt-3 text-sm" data-testid="diagnostics-gdi">{{ gdiText }}</div>
          <div v-if="snap.capture.error" class="mt-1 break-words text-sm" style="color: #e5484d">{{ t('remote.diagnostics.captureError', { msg: snap.capture.error }) }}</div>
          <template v-if="snap.capture.duplication.length">
            <div class="mb-1 mt-3 text-xs font-medium" style="color: var(--pc-text-muted)">{{ t('remote.diagnostics.duplication') }}</div>
            <el-table :data="snap.capture.duplication" size="small" border class="w-full" data-testid="diagnostics-duplication">
              <el-table-column :label="t('remote.diagnostics.output')" prop="output" min-width="130" />
              <el-table-column :label="t('remote.diagnostics.adapter')" min-width="180">
                <template #default="{ row }">{{ row.adapter || '—' }}</template>
              </el-table-column>
              <el-table-column :label="t('remote.diagnostics.duplication')" min-width="100">
                <template #default="{ row }">
                  <el-tag size="small" round :type="row.ok ? 'success' : 'danger'">{{ row.ok ? t('remote.diagnostics.usable') : t('remote.diagnostics.unusable') }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column :label="t('remote.diagnostics.firstFrameMs')" min-width="80">
                <template #default="{ row }">{{ num(row.firstFrameMs) }}</template>
              </el-table-column>
              <!-- 画面在动时每幅新画面多久（0.8.7 起的被控端才报） -->
              <el-table-column :label="t('remote.diagnostics.frameMs')" min-width="150">
                <template #default="{ row }">{{ row.frameMs != null ? t('remote.diagnostics.frameValue', { avg: num(row.frameMs), p90: num(row.frameP90Ms), size: row.frameSize ?? '' }) : '—' }}</template>
              </el-table-column>
              <el-table-column :label="t('remote.diagnostics.error')" min-width="200">
                <template #default="{ row }"><span style="color: #e5484d">{{ row.error || '' }}</span></template>
              </el-table-column>
            </el-table>
          </template>
        </section>

        <!-- 最近的远程（新的在前） -->
        <section data-testid="diagnostics-sessions">
          <h3 class="mb-2 text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('remote.diagnostics.sessions') }}</h3>
          <div v-if="!sessions.length" class="text-sm" style="color: var(--pc-text-muted)">{{ t('remote.diagnostics.noSessions') }}</div>
          <el-table v-else :data="sessions" size="small" border row-key="sessionId" class="w-full">
            <el-table-column type="expand">
              <template #default="{ row }">
                <div class="flex flex-col gap-3 px-3 py-2" data-testid="diagnostics-session-detail">
                  <div v-for="side in sides(asSession(row))" :key="side.key">
                    <div class="mb-1 text-xs font-medium">{{ side.label }}</div>
                    <div v-if="!side.fields.length" class="text-xs" style="color: var(--pc-text-muted)">{{ side.empty }}</div>
                    <dl v-else class="grid gap-x-3 gap-y-0.5 text-xs" style="grid-template-columns: minmax(8rem, max-content) minmax(0, 1fr)">
                      <template v-for="f in side.fields" :key="f.key">
                        <dt style="color: var(--pc-text-muted)">{{ f.key }}</dt>
                        <dd class="min-w-0 break-words">{{ f.value }}</dd>
                      </template>
                    </dl>
                  </div>
                  <div><el-button size="small" @click="copy(row)">{{ t('remote.diagnostics.copySession') }}</el-button></div>
                </div>
              </template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.when')" min-width="130">
              <template #default="{ row }">
                <span
                  data-testid="diagnostics-session" :data-session-id="row.sessionId" :data-agent="row.agent ? 'true' : 'false'" :data-viewer="row.viewer ? 'true' : 'false'"
                  :data-sent-fps="row.agent?.sentFps?.p50 ?? ''" :data-viewer-fps="row.viewer?.decodedFps?.p50 ?? ''"
                  :data-browser="row.viewer?.browser ?? ''" :data-freezes="row.viewer?.freezes ?? ''" :data-encoder="row.agent?.encoder ?? ''"
                >{{ formatWhen(row.startedAt) }}</span>
                <div class="text-xs" style="color: var(--pc-text-muted)">{{ sessionDuration(asSession(row)) }}</div>
              </template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.who')" min-width="110">
              <template #default="{ row }">{{ row.controllerName }} · {{ row.mode === 'View' ? t('remote.modeView') : t('remote.modeControl') }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.line')" min-width="140">
              <template #default="{ row }">{{ lineText(asSession(row)) }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.settings')" min-width="220">
              <template #default="{ row }">{{ settingsText(row.agent?.settings) }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.sizes')" min-width="150">
              <template #default="{ row }">{{ sizesText(row.agent?.sizes) }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.sentFps')" min-width="100">
              <template #default="{ row }">{{ pair(row.agent?.sentFps, 'p50', 'p10') }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.viewerFps')" min-width="110">
              <template #default="{ row }">{{ pair(row.viewer?.decodedFps, 'p50', 'p10') }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.freezes')" min-width="110">
              <template #default="{ row }">{{ freezeText(asSession(row)) }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.captureMs')" min-width="100">
              <template #default="{ row }">{{ pair(row.agent?.captureMs, 'p50', 'p90') }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.encodeMs')" min-width="100">
              <template #default="{ row }">{{ pair(row.agent?.encodeMs, 'p50', 'p90') }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.cpuBound')" min-width="80">
              <template #default="{ row }">{{ row.agent ? durationText(row.agent.cpuBoundSeconds) : '—' }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.rttMs')" min-width="100">
              <template #default="{ row }">{{ pair(row.agent?.rttMs, 'p50', 'p90') }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.loss')" min-width="100">
              <template #default="{ row }">{{ pair(row.viewer?.lossPermille ?? row.agent?.lossPermille, 'p50', 'p90') }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.auto')" min-width="120">
              <template #default="{ row }">{{ autoText(asSession(row)) }}</template>
            </el-table-column>
            <el-table-column :label="t('remote.diagnostics.errors')" min-width="220">
              <template #default="{ row }">
                <span class="line-clamp-3 break-words" :title="errorsOf(asSession(row)).join('\n')" :style="errorsOf(asSession(row)).length ? 'color: #e5484d' : undefined">{{ errorsOf(asSession(row)).join('；') || '—' }}</span>
              </template>
            </el-table-column>
          </el-table>
        </section>

        <el-input v-if="rawText" type="textarea" :model-value="rawText" readonly :rows="8" data-testid="diagnostics-raw" />
      </template>
    </div>
    <template #footer>
      <el-button :loading="loading" data-testid="diagnostics-refresh" @click="load">{{ t('remote.diagnostics.refresh') }}</el-button>
      <el-button type="primary" :disabled="!data" data-testid="diagnostics-copy" @click="copy(data)">{{ t('remote.diagnostics.copy') }}</el-button>
    </template>
  </el-dialog>
</template>
