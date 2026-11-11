<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'

// 一个远端参与者的声音。单独成组件：通话界面最小化、切换布局时声音不能断。
const props = defineProps<{ track: MediaStreamTrack; participantId: string }>()
const emit = defineEmits<{ blocked: [] }>()
const audio = ref<HTMLAudioElement | null>(null)

function attach(): void {
  const el = audio.value
  if (!el) return
  el.srcObject = new MediaStream([props.track])
  // 页面从没被点过时浏览器会拦自动播放：告诉上层显示"点击开启声音"
  el.play().catch(() => emit('blocked'))
}

/** 用户点了"开启声音"后由上层调用 */
function resume(): void {
  void audio.value?.play().catch(() => undefined)
}
defineExpose({ resume })

onMounted(attach)
watch(() => props.track, attach)
onBeforeUnmount(() => { if (audio.value) audio.value.srcObject = null })
</script>

<template>
  <audio ref="audio" autoplay playsinline data-testid="call-audio" :data-participant-id="participantId" />
</template>
