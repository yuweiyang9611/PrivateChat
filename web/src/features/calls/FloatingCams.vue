<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useElementSize, useStorage } from '@vueuse/core'
import { Minus, VideoCamera } from '@element-plus/icons-vue'
import ParticipantTile from './ParticipantTile.vue'
import { attachTrack } from './attachTrack'
import { cornerStyle, isCorner, useFloatDrag, type Corner } from './floating'
import { useUiStore } from '@/stores/ui'

/**
 * 大图里的摄像头小窗：浮在共享的画面上，拖到哪个角就停在哪个角（记在这台设备上）；
 * 点"收起"缩成一个圆形的浮动图标，点图标再展开。最多显示两个人，多的写"+N"。
 * 放在 ScreenStage 的浮层里，所以不会挡住大图的按钮。
 */
export interface FloatTile { key: string; userId: string; name: string; track?: MediaStreamTrack; showVideo: boolean; micOn?: boolean }
const props = defineProps<{ people: FloatTile[] }>()
/** 收没收成图标：放在外面（CallOverlay），换着看别人的屏幕时不变，退出大图才还原 */
const collapsed = defineModel<boolean>('collapsed', { default: false })

const { t } = useI18n()
const ui = useUiStore()
const root = ref<HTMLElement | null>(null)
const layer = ref<HTMLElement | null>(null)
onMounted(() => { layer.value = root.value?.parentElement ?? null })
const { width: layerW, height: layerH } = useElementSize(layer)

const corner = useStorage<Corner>('pc.call.floatCorner', 'br')
if (!isCorner(corner.value)) corner.value = 'br'
const { drag, handlers, clicked } = useFloatDrag(root, corner, layer)

const gap = computed(() => (ui.elderMode ? 16 : 12))
const style = computed(() => {
  if (drag.value) return { left: `${drag.value.x}px`, top: `${drag.value.y}px` }
  const g = `${gap.value}px`
  return cornerStyle(corner.value, { top: g, right: g, bottom: g, left: g })
})
/** 小窗宽度：地方窄或者矮（手机竖着、横着）就小一点 */
const small = computed(() => layerW.value > 0 && (layerW.value < 700 || layerH.value < 500))
const tileW = computed(() => (small.value ? (ui.elderMode ? 140 : 120) : ui.elderMode ? 240 : 200))
const shown = computed(() => props.people.slice(0, 2))
const more = computed(() => props.people.length - shown.value.length)

// 收起后的图标：第一个人的摄像头画面（关着就显示头像）
const first = computed(() => props.people[0] ?? null)
const iconVideo = ref<HTMLVideoElement | null>(null)
watch(() => [first.value?.showVideo ? first.value.track : null, iconVideo.value] as const, ([track, el]) => attachTrack(el, track ?? null), { immediate: true, flush: 'post' })
onBeforeUnmount(() => { if (iconVideo.value) iconVideo.value.srcObject = null })

function collapse(): void {
  if (clicked()) collapsed.value = true
}
function expand(): void {
  if (clicked()) collapsed.value = false
}
</script>

<template>
  <div
    ref="root"
    class="pc-float"
    :class="{ 'is-dragging': !!drag }"
    :style="style"
    data-testid="call-float"
    :data-collapsed="collapsed"
    :data-corner="corner"
    v-on="handlers"
  >
    <div v-if="!collapsed" class="pc-float-window" :style="{ width: `${tileW}px` }">
      <ParticipantTile
        v-for="x in shown"
        :key="x.key"
        class="aspect-[4/3] w-full shrink-0"
        compact
        :name="x.name"
        :track="x.track"
        :show-video="x.showVideo"
        :mic-on="x.micOn"
        :user-id="x.userId"
        kind="camera"
      />
      <div v-if="more > 0" class="text-center text-xs opacity-80">+{{ more }}</div>
      <button class="pc-float-collapse" type="button" :title="t('calls.floatHide')" :aria-label="t('calls.floatHide')" data-testid="call-float-collapse" @click="collapse">
        <el-icon :size="ui.elderMode ? 22 : 18"><Minus /></el-icon>
      </button>
    </div>
    <button v-else class="pc-float-icon" type="button" :title="t('calls.floatShow')" :aria-label="t('calls.floatShow')" data-testid="call-float-icon" @click="expand">
      <video v-show="first?.showVideo && first.track" ref="iconVideo" class="h-full w-full object-cover" autoplay playsinline muted />
      <span v-if="!(first?.showVideo && first.track)" class="text-xl font-semibold">{{ first?.name.slice(0, 1) }}</span>
      <span class="pc-float-badge"><el-icon :size="12"><VideoCamera /></el-icon></span>
    </button>
  </div>
</template>

<style scoped>
.pc-float {
  position: absolute;
  z-index: 2;
  pointer-events: auto;
  touch-action: none;
  user-select: none;
  -webkit-user-select: none;
  cursor: grab;
}
.pc-float.is-dragging {
  cursor: grabbing;
}
.pc-float-window {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 6px;
  border-radius: 16px;
  background: rgba(27, 23, 20, 0.85);
  border: 1px solid rgba(255, 255, 255, 0.2);
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.45);
  color: #fff;
}
/* 收起按钮：压在小窗右上角，长辈模式更大 */
.pc-float-collapse {
  position: absolute;
  top: 10px;
  right: 10px;
  display: flex;
  height: 32px;
  width: 32px;
  align-items: center;
  justify-content: center;
  border-radius: 9999px;
  background: rgba(0, 0, 0, 0.6);
  color: #fff;
}
:global(html.elder) .pc-float-collapse {
  height: 44px;
  width: 44px;
}
.pc-float-icon {
  position: relative;
  display: flex;
  height: 60px;
  width: 60px;
  align-items: center;
  justify-content: center;
  overflow: visible;
  border-radius: 9999px;
  background: var(--pc-primary);
  color: #fff;
  border: 2px solid rgba(255, 255, 255, 0.85);
  box-shadow: 0 6px 18px rgba(0, 0, 0, 0.5);
}
:global(html.elder) .pc-float-icon {
  height: 72px;
  width: 72px;
}
.pc-float-icon video {
  border-radius: 9999px;
}
.pc-float-badge {
  position: absolute;
  right: -2px;
  bottom: -2px;
  display: flex;
  height: 22px;
  width: 22px;
  align-items: center;
  justify-content: center;
  border-radius: 9999px;
  background: #1b1714;
  color: #fff;
  border: 1px solid rgba(255, 255, 255, 0.6);
}
</style>
