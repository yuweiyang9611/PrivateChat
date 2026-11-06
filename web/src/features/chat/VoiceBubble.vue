<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { VideoPlay, VideoPause } from '@element-plus/icons-vue'

const props = defineProps<{
  src: string
  /** 秒 */
  duration?: number
  pending?: boolean
}>()

const audio = ref<HTMLAudioElement | null>(null)
const playing = ref(false)
const current = ref(0)
const total = computed(() => props.duration ?? audio.value?.duration ?? 0)

/** 气泡宽度随时长增长：1 s 约 130 px，60 s 封顶 260 px */
const width = computed(() => Math.round(Math.min(260, 120 + (total.value || 1) * 2.4)))
const progress = computed(() => (total.value ? Math.min(100, (current.value / total.value) * 100) : 0))

function fmt(s: number): string {
  const m = Math.floor(s / 60)
  const r = Math.round(s % 60)
  return m ? `${m}:${String(r).padStart(2, '0')}` : `${r}"`
}

/** 同一时间只播一条语音 */
let active: HTMLAudioElement | null = null
function toggle(): void {
  const el = audio.value
  if (!el || props.pending) return
  if (playing.value) {
    el.pause()
    return
  }
  if (active && active !== el) active.pause()
  active = el
  void el.play()
}

function onEnded(): void {
  playing.value = false
  current.value = 0
}

watch(() => props.src, () => { playing.value = false; current.value = 0 })
onBeforeUnmount(() => audio.value?.pause())
</script>

<template>
  <div class="flex items-center gap-2 select-none" :style="{ width: width + 'px' }" @click="toggle">
    <audio
      ref="audio"
      :src="src"
      preload="metadata"
      @play="playing = true"
      @pause="playing = false"
      @timeupdate="current = audio?.currentTime ?? 0"
      @ended="onEnded"
    />
    <button class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full" style="background: rgba(255, 255, 255, 0.25)" :disabled="pending" type="button">
      <el-icon :size="20"><VideoPause v-if="playing" /><VideoPlay v-else /></el-icon>
    </button>
    <div class="flex-1">
      <div class="h-1.5 w-full overflow-hidden rounded-full" style="background: rgba(255, 255, 255, 0.3)">
        <div class="h-full rounded-full" style="background: currentColor; transition: width 0.2s linear" :style="{ width: progress + '%' }" />
      </div>
    </div>
    <span class="shrink-0 text-sm tabular-nums">{{ fmt(playing ? current : total) }}</span>
  </div>
</template>
