<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { Mute } from '@element-plus/icons-vue'

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
}>()

const video = ref<HTMLVideoElement | null>(null)

function attach(): void {
  const el = video.value
  if (!el) return
  const track = props.showVideo ? props.track : null
  // 同一条轨道不重复设置，否则画面会闪一下
  const current = (el.srcObject as MediaStream | null)?.getVideoTracks()[0]
  if (current === (track ?? undefined)) return
  el.srcObject = track ? new MediaStream([track]) : null
  if (track) void el.play().catch(() => undefined)
}

watch(() => [props.track, props.showVideo, video.value], attach, { immediate: true, flush: 'post' })
onBeforeUnmount(() => { if (video.value) video.value.srcObject = null })
</script>

<template>
  <div
    class="relative flex min-h-0 items-center justify-center overflow-hidden rounded-2xl"
    style="background: #2a2420"
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
    <div v-if="!(showVideo && track)" class="flex flex-col items-center gap-3 p-4">
      <el-avatar :size="kind === 'self' ? 48 : 96" style="background: var(--pc-primary); font-size: 2rem">{{ name.slice(0, 1) }}</el-avatar>
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
