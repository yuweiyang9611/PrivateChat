<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { Mute } from '@element-plus/icons-vue'
import { attachTrack } from './attachTrack'

const props = defineProps<{
  name: string
  /** 有画面可放时传轨道；showVideo 为 false（对方关了摄像头）时显示头像 */
  track?: MediaStreamTrack | null
  showVideo?: boolean
  micOn?: boolean
  /** 屏幕共享：完整显示不裁剪 */
  contain?: boolean
  /** 自己的预览：镜像 */
  mirror?: boolean
  badge?: string
  userId?: string
  kind?: 'camera' | 'screen' | 'self'
  /** 小格子（有人共享屏幕时旁边那一列、下面那一排）：头像小一点 */
  compact?: boolean
}>()

const video = ref<HTMLVideoElement | null>(null)

watch(() => [props.track, props.showVideo, video.value], () => attachTrack(video.value, props.showVideo ? props.track : null), { immediate: true, flush: 'post' })
onBeforeUnmount(() => { if (video.value) video.value.srcObject = null })
</script>

<template>
  <div
    class="relative flex min-h-0 items-center justify-center overflow-hidden rounded-2xl"
    :style="{ background: kind === 'screen' ? '#000' : '#2a2420' }"
    data-testid="call-tile"
    :data-user-id="userId"
    :data-kind="kind ?? 'camera'"
    :data-video="showVideo && track ? 'on' : 'off'"
  >
    <!-- 视频始终静音：声音走单独的 audio 元素，最小化时也不断 -->
    <video
      v-show="showVideo && track"
      ref="video"
      class="h-full w-full"
      :class="[contain ? 'object-contain' : 'object-cover', mirror ? '-scale-x-100' : '']"
      autoplay
      playsinline
      muted
    />
    <div v-if="!(showVideo && track) && kind !== 'screen'" class="flex flex-col items-center gap-3" :class="compact ? 'p-1' : 'p-4'">
      <el-avatar :size="compact ? (kind === 'self' ? 32 : 48) : kind === 'self' ? 48 : 96" :style="{ background: 'var(--pc-primary)', fontSize: compact ? '1.25rem' : '2rem' }">{{ name.slice(0, 1) }}</el-avatar>
    </div>
    <div
      v-if="kind !== 'self'"
      class="absolute bottom-2 left-2 flex max-w-[calc(100%-1rem)] items-center gap-1 rounded-full px-2.5 py-1 text-xs text-white"
      style="background: rgba(0, 0, 0, 0.55)"
    >
      <el-icon v-if="micOn === false" :size="14" data-testid="tile-muted"><Mute /></el-icon>
      <span class="truncate">{{ name }}<template v-if="badge">（{{ badge }}）</template></span>
    </div>
  </div>
</template>
