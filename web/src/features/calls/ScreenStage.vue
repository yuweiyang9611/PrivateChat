<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useElementSize, useEventListener, useMediaQuery } from '@vueuse/core'
import { Close, RefreshLeft, ZoomIn, ZoomOut } from '@element-plus/icons-vue'
import { anchorScroll, bigViewSide, nextZoom } from './stageLayout'
import { attachTrack } from './attachTrack'
import { useUiStore } from '@/stores/ui'

/**
 * 别人共享的屏幕（设计稿 5.5"有人共享屏幕时"）。
 * 平时：按画面真实的比例摆成 frameW × frameH（CallOverlay 算好的），点一下进入大图。
 * 大图（expanded）：铺满整个通话界面，按钮放在画面用不上的那一边；只有大图里能放大、拖动，退出就还原。
 */
const props = defineProps<{
  userId: string
  name: string
  track?: MediaStreamTrack | null
  frameW: number
  frameH: number
  /** 外面现在按什么比例摆的（重建这个组件时从这里接着量，不从 16:9 重新来） */
  initialAspect?: number
  expanded: boolean
  /** 大图里顶上的提示（正在重新连接之类） */
  notice?: string | null
}>()
const emit = defineEmits<{ expand: []; collapse: []; aspect: [number] }>()

const { t } = useI18n()
const ui = useUiStore()
const coarse = useMediaQuery('(pointer: coarse)')

const root = ref<HTMLElement | null>(null)
const box = ref<HTMLElement | null>(null)
const video = ref<HTMLVideoElement | null>(null)
const { width: bw, height: bh } = useElementSize(box)
const { width: rw, height: rh } = useElementSize(root)

/** 画面的宽高比：量到之前按外面现在用的（默认 16:9）；量到的和它差 2% 以上才告诉外面 */
const ar = ref(props.initialAspect && props.initialAspect > 0 ? props.initialAspect : 16 / 9)
const zoom = ref(1)
const baseW = computed(() => Math.max(0, Math.min(bw.value, bh.value * ar.value)))
const vw = computed(() => baseW.value * zoom.value)
const vh = computed(() => (baseW.value / ar.value) * zoom.value)
/** 最多放大 4 倍，video 元素最宽 8192 px */
const maxZoom = computed(() => Math.max(1, Math.min(4, 8192 / Math.max(1, baseW.value))))

function readSize(): void {
  const el = video.value
  if (!el || !el.videoWidth || !el.videoHeight) return
  const next = el.videoWidth / el.videoHeight
  if (Math.abs(next - ar.value) / ar.value <= 0.02) return
  ar.value = next
  zoom.value = 1
  emit('aspect', next)
}

watch(() => [props.track, video.value] as const, ([track, el]) => attachTrack(el, track ?? null), { immediate: true, flush: 'post' })
// Safari 上 MediaStream 的 resize 事件不可靠：每秒再看一眼
let sizeTimer: ReturnType<typeof setInterval> | undefined
onMounted(() => { sizeTimer = setInterval(readSize, 1000) })
onBeforeUnmount(() => {
  clearInterval(sizeTimer)
  if (video.value) video.value.srcObject = null
})

// ---------- 大图里的按钮：放在画面用不上的那一边，一直显示 ----------
const btn = computed(() => (ui.elderMode ? 76 : 64))
const railR = computed(() => btn.value + 16)
const side = computed(() => bigViewSide(rw.value, rh.value, ar.value, railR.value))

// 竖着拿手机看横的屏幕：提示横过来（等安卓自己转过来的那一下再出，过几秒自己消失）
const wantHint = computed(() => props.expanded && coarse.value && rh.value > rw.value && ar.value > 1.2)
const hint = ref(false)
let hintTimer: ReturnType<typeof setTimeout> | undefined
watch(wantHint, (want) => {
  clearTimeout(hintTimer)
  hint.value = false
  if (!want) return
  hintTimer = setTimeout(() => {
    hint.value = true
    hintTimer = setTimeout(() => { hint.value = false }, ui.elderMode ? 8000 : 5000)
  }, 600)
})
onBeforeUnmount(() => clearTimeout(hintTimer))

watch(() => props.expanded, (on) => {
  if (!on) zoom.value = 1
  else void nextTick(() => box.value?.focus({ preventScroll: true }))
})

// ---------- 放大、拖动（只在大图里） ----------
function zoomTo(z: number, ax = bw.value / 2, ay = bh.value / 2): void {
  const el = box.value
  if (!el) return
  const nz = Math.min(maxZoom.value, Math.max(1, z))
  if (Math.abs(nz - zoom.value) < 0.001) return
  const sl = el.scrollLeft
  const st = el.scrollTop
  const ow = vw.value
  const oh = vh.value
  zoom.value = nz
  void nextTick(() => {
    el.scrollLeft = anchorScroll(sl, ax, bw.value, ow, vw.value)
    el.scrollTop = anchorScroll(st, ay, bh.value, oh, vh.value)
  })
}
const zoomStep = (dir: 1 | -1) => zoomTo(nextZoom(zoom.value, dir, maxZoom.value))

const pointers = new Map<number, { x: number; y: number }>()
let pinch: { d0: number; z0: number; mx: number; my: number } | null = null
let press: { x: number; y: number; t: number; moved: boolean; multi: boolean } | null = null
let lastTap: { t: number; x: number; y: number } | null = null
const panning = ref(false)

function boxPoint(x: number, y: number): { x: number; y: number } {
  const r = box.value!.getBoundingClientRect()
  return { x: x - r.left, y: y - r.top }
}
function twoFingers(): { d: number; mx: number; my: number } {
  const [a, b] = [...pointers.values()] as [{ x: number; y: number }, { x: number; y: number }]
  const m = boxPoint((a.x + b.x) / 2, (a.y + b.y) / 2)
  return { d: Math.max(1, Math.hypot(a.x - b.x, a.y - b.y)), mx: m.x, my: m.y }
}

function onPointerDown(e: PointerEvent): void {
  if (!props.expanded || !box.value) return
  if (e.pointerType === 'mouse' && e.button !== 0) return
  box.value.setPointerCapture?.(e.pointerId)
  pointers.set(e.pointerId, { x: e.clientX, y: e.clientY })
  if (pointers.size === 1) press = { x: e.clientX, y: e.clientY, t: performance.now(), moved: false, multi: false }
  else if (pointers.size === 2) {
    const f = twoFingers()
    pinch = { d0: f.d, z0: zoom.value, mx: f.mx, my: f.my }
    if (press) press.multi = true
  }
}

function onPointerMove(e: PointerEvent): void {
  const p = pointers.get(e.pointerId)
  const el = box.value
  if (!p || !el) return
  const dx = e.clientX - p.x
  const dy = e.clientY - p.y
  p.x = e.clientX
  p.y = e.clientY
  if (pinch && pointers.size >= 2) {
    const f = twoFingers()
    el.scrollLeft -= f.mx - pinch.mx
    el.scrollTop -= f.my - pinch.my
    pinch.mx = f.mx
    pinch.my = f.my
    zoomTo(pinch.z0 * (f.d / pinch.d0), f.mx, f.my)
    return
  }
  if (!press) return
  if (!press.moved && Math.hypot(e.clientX - press.x, e.clientY - press.y) > 6) press.moved = true
  if (press.moved && zoom.value > 1.01) {
    el.scrollLeft -= dx
    el.scrollTop -= dy
    panning.value = true
  }
}

function onPointerUp(e: PointerEvent): void {
  if (!pointers.delete(e.pointerId)) return
  if (pointers.size < 2) pinch = null
  if (pointers.size > 0) return
  panning.value = false
  const p = press
  press = null
  if (e.type === 'pointercancel' || !p || p.moved || p.multi || performance.now() - p.t > 300 || !box.value) return
  // 点两下：放大到 2 倍（放大着就还原），点在哪儿就放大哪儿。鼠标双击也走这里
  const now = performance.now()
  const at = boxPoint(e.clientX, e.clientY)
  if (lastTap && now - lastTap.t < 300 && Math.hypot(at.x - lastTap.x, at.y - lastTap.y) < 30) {
    lastTap = null
    zoomTo(zoom.value > 1.01 ? 1 : 2, at.x, at.y)
  } else lastTap = { t: now, ...at }
}

/** 指针在画面上的位置；在画面外（摄像头小窗、按钮上）就按画面中心 */
function anchorAt(x: number, y: number): { x: number; y: number } {
  const at = boxPoint(x, y)
  return at.x >= 0 && at.y >= 0 && at.x <= bw.value && at.y <= bh.value ? at : { x: bw.value / 2, y: bh.value / 2 }
}

// Ctrl + 滚轮（Chrome / Edge 的触控板捏合也是这个）放大；普通滚轮就是滚动（拖着看）。
// 挂在整个大图上：指针在摄像头小窗、按钮上时也不能让浏览器把整个页面放大
useEventListener(root, 'wheel', (e: WheelEvent) => {
  if (!props.expanded || !(e.ctrlKey || e.metaKey) || !box.value) return
  e.preventDefault()
  const at = anchorAt(e.clientX, e.clientY)
  zoomTo(zoom.value * Math.exp(-e.deltaY * 0.01), at.x, at.y)
}, { passive: false })

// Safari：别让双指捏合把整个页面放大；Mac 触控板的捏合只发 gesture 事件（不是 Ctrl + 滚轮），在大图里拿来放大。
// 手机上双指捏合已经由上面的指针事件处理了（这时有按着的手指），这里不重复放大
type GestureLike = Event & { scale: number; clientX: number; clientY: number }
let gesture: { z0: number; x: number; y: number } | null = null
useEventListener(root, 'gesturestart', (e: Event) => {
  e.preventDefault()
  const g = e as GestureLike
  gesture = props.expanded && pointers.size === 0 && box.value ? { z0: zoom.value, ...anchorAt(g.clientX, g.clientY) } : null
}, { passive: false })
useEventListener(root, 'gesturechange', (e: Event) => {
  e.preventDefault()
  if (!gesture || !props.expanded || pointers.size > 0) return
  zoomTo(gesture.z0 * (e as GestureLike).scale, gesture.x, gesture.y)
}, { passive: false })
useEventListener(root, 'gestureend', () => { gesture = null })

/** 再放大一档还有没有用（最大倍数不是整档时，到最后一档就该灰掉） */
const canZoomIn = computed(() => nextZoom(zoom.value, 1, maxZoom.value) > zoom.value + 0.001)

function onBoxClick(): void {
  if (!props.expanded) emit('expand')
}
</script>

<template>
  <section
    ref="root"
    class="pc-stage"
    :class="{ 'is-expanded': expanded, 'is-bottom': expanded && side === 'bottom', 'is-zoomed': zoom > 1.01 }"
    :style="expanded ? undefined : { width: `${frameW}px`, height: `${frameH}px` }"
    data-testid="call-tile"
    data-kind="screen"
    :data-user-id="userId"
    :data-video="track ? 'on' : 'off'"
    :data-zoom="String(Math.round(zoom * 100) / 100)"
  >
    <div class="pc-stage-view">
      <div
        ref="box"
        class="pc-stage-box"
        :class="{ 'is-panning': panning }"
        tabindex="0"
        :role="expanded ? undefined : 'button'"
        :aria-label="t('calls.screenOf', { name })"
        :title="expanded ? undefined : t('calls.fullscreen')"
        data-testid="call-stage"
        @click="onBoxClick"
        @keydown.enter.prevent="onBoxClick"
        @keydown.space.prevent="onBoxClick"
        @pointerdown="onPointerDown"
        @pointermove="onPointerMove"
        @pointerup="onPointerUp"
        @pointercancel="onPointerUp"
      >
        <!-- 声音走单独的 audio 元素：这里始终静音 -->
        <video
          ref="video"
          class="pc-stage-video"
          :style="{ width: `${vw}px`, height: `${vh}px` }"
          autoplay
          playsinline
          muted
          @loadedmetadata="readSize"
          @resize="readSize"
        />
        <div v-if="!track" class="pc-stage-waiting">{{ t('calls.screenWaiting', { name }) }}</div>
      </div>
      <!-- 大图里浮在画面上的东西（摄像头小窗）：只盖画面，不盖按钮 -->
      <div v-if="expanded" class="pc-stage-float"><slot name="float" /></div>
    </div>

    <nav v-if="expanded" class="pc-bigview-bar" :style="side === 'right' ? { width: `${railR}px` } : { height: `${railR}px` }">
      <button class="pc-bigview-btn primary" :style="{ width: `${btn}px`, height: `${btn}px` }" type="button" data-testid="call-exit-fullscreen" @click="emit('collapse')">
        <el-icon :size="22"><Close /></el-icon>
        <span>{{ t('calls.exitFullscreen') }}</span>
      </button>
      <button class="pc-bigview-btn" :style="{ width: `${btn}px`, height: `${btn}px` }" type="button" :disabled="!canZoomIn" data-testid="call-zoom-in" @click="zoomStep(1)">
        <el-icon :size="22"><ZoomIn /></el-icon>
        <span>{{ t('calls.zoomIn') }}</span>
      </button>
      <button class="pc-bigview-btn" :style="{ width: `${btn}px`, height: `${btn}px` }" type="button" :disabled="zoom <= 1.01" data-testid="call-zoom-out" @click="zoomStep(-1)">
        <el-icon :size="22"><ZoomOut /></el-icon>
        <span>{{ t('calls.zoomOut') }}</span>
      </button>
      <button class="pc-bigview-btn" :style="{ width: `${btn}px`, height: `${btn}px` }" type="button" :disabled="zoom <= 1.01" data-testid="call-zoom-reset" @click="zoomTo(1)">
        <el-icon :size="22"><RefreshLeft /></el-icon>
        <span>{{ t('calls.zoomReset') }}</span>
      </button>
    </nav>

    <div v-if="expanded && notice" class="pc-stage-chip is-top">{{ notice }}</div>
    <!-- 按钮在下面一排时，提示放在按钮上面，别压着按钮 -->
    <div
      v-if="hint"
      class="pc-stage-chip is-bottom"
      :style="side === 'bottom' ? { bottom: `calc(${railR + 16}px + env(safe-area-inset-bottom))` } : undefined"
      data-testid="call-rotate-hint"
    >
      {{ t('calls.rotateHint') }}
    </div>
  </section>
</template>

<style scoped>
.pc-stage {
  position: relative;
  display: flex;
  min-width: 0;
  min-height: 0;
}
/* 铺满：父级给的 justify-self / align-self: center 对 fixed 的元素也起作用（会缩成内容大小再居中），这里改回 stretch */
.pc-stage.is-expanded {
  position: fixed;
  inset: 0;
  justify-self: stretch;
  align-self: stretch;
  width: auto;
  height: auto;
  margin: 0;
  z-index: 10;
  flex-direction: row;
  background: #000;
  padding: env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom) env(safe-area-inset-left);
}
.pc-stage.is-bottom {
  flex-direction: column;
}
.pc-stage-view {
  position: relative;
  display: flex;
  flex: 1 1 0;
  min-width: 0;
  min-height: 0;
}
.pc-stage-float {
  position: absolute;
  inset: 0;
  pointer-events: none;
  overflow: hidden;
}
.pc-stage-box {
  position: relative;
  display: grid;
  flex: 1 1 0;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  border-radius: 8px;
  background: #000;
  cursor: zoom-in;
  outline: none;
}
.pc-stage-box:focus-visible {
  box-shadow: inset 0 0 0 2px var(--pc-primary);
}
.is-expanded .pc-stage-box {
  overflow: auto;
  border-radius: 0;
  cursor: default;
  touch-action: none;
  overscroll-behavior: contain;
  user-select: none;
  -webkit-user-select: none;
  -webkit-touch-callout: none;
  scrollbar-width: thin;
  scrollbar-color: rgba(255, 255, 255, 0.35) transparent;
}
.is-expanded.is-zoomed .pc-stage-box {
  cursor: grab;
}
.is-expanded .pc-stage-box.is-panning {
  cursor: grabbing;
}
/* 画面比可视区域小时居中；放大以后从左上角开始滚。max-width: none 盖掉 Tailwind 给 video 的 max-width: 100% */
.pc-stage-video {
  display: block;
  margin: auto;
  max-width: none;
  object-fit: contain;
  background: #000;
  pointer-events: none;
}
.pc-bigview-bar {
  flex: none;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 8px;
}
.is-bottom .pc-bigview-bar {
  flex-direction: row;
}
.pc-bigview-btn {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 2px;
  border-radius: 16px;
  background: rgba(255, 255, 255, 0.14);
  color: #fff;
  font-size: 0.75rem;
  line-height: 1.1;
}
.pc-bigview-btn.primary {
  background: #fff;
  color: #1b1714;
  font-weight: 600;
}
.pc-bigview-btn:disabled {
  opacity: 0.4;
}
/* 盖在摄像头小窗（z-index 2）上面：小窗拖到上面的角时也看得见提示 */
.pc-stage-chip {
  position: absolute;
  z-index: 3;
  left: 50%;
  transform: translateX(-50%);
  pointer-events: none;
  border-radius: 9999px;
  padding: 6px 14px;
  background: rgba(0, 0, 0, 0.65);
  color: #fff;
  font-size: 0.875rem;
  white-space: nowrap;
}
.pc-stage-chip.is-top {
  top: calc(12px + env(safe-area-inset-top));
}
.pc-stage-chip.is-bottom {
  bottom: calc(24px + env(safe-area-inset-bottom));
}
.pc-stage-waiting {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0 12px;
  text-align: center;
  color: rgba(255, 255, 255, 0.7);
  font-size: 0.875rem;
}
</style>
