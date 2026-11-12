<script setup lang="ts">
import { ref, watch } from 'vue'
import { blobUrl } from '@/upload'
import { formatDuration } from '@/features/drive/fileKind'

/** 一格照片 / 视频缩略图：缩略图还没生成（或失败）时显示图标，视频角落标时长 */
const props = defineProps<{ blobId: string; mime: string; durationSeconds?: number | null; size?: 'thumb-320' | 'thumb-1280'; label?: string }>()
defineEmits<{ open: [] }>()
const broken = ref(false)
watch(() => props.blobId, () => { broken.value = false })
</script>

<template>
  <div class="relative h-full w-full overflow-hidden" style="background: var(--pc-border)" :data-blob="blobId">
    <button type="button" class="flex h-full w-full items-center justify-center" :aria-label="label ?? ''" @click="$emit('open')">
      <img
        v-if="!broken"
        :src="blobUrl(blobId, { v: size ?? 'thumb-320' })"
        alt=""
        loading="lazy"
        class="h-full w-full object-cover"
        @error="broken = true"
      />
      <span v-else class="text-3xl">{{ mime.startsWith('video/') ? '🎞️' : '🖼️' }}</span>
    </button>
    <span
      v-if="mime.startsWith('video/')"
      class="pointer-events-none absolute bottom-1 right-1 rounded px-1 text-[11px] text-white"
      style="background: rgba(0, 0, 0, 0.55)"
    >▶ {{ formatDuration(durationSeconds) }}</span>
    <slot />
  </div>
</template>
