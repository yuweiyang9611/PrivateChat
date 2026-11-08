<script setup lang="ts">
import { computed, onBeforeUnmount } from 'vue'
import { useI18n } from 'vue-i18n'
import { imageViewer, mediaDialog } from './preview'

const { t } = useI18n()
const dialogOpen = computed({
  get: () => mediaDialog.value !== null,
  set: (v) => { if (!v) mediaDialog.value = null },
})
// 预览状态是模块级的：用系统返回键离开网盘时要清掉，否则下次进来视频会自己弹出来接着放
onBeforeUnmount(() => {
  imageViewer.value = null
  mediaDialog.value = null
})
</script>

<template>
  <el-image-viewer
    v-if="imageViewer"
    :url-list="imageViewer.urls"
    :initial-index="imageViewer.index"
    teleported
    hide-on-click-modal
    @close="imageViewer = null"
  />
  <el-dialog v-model="dialogOpen" :title="mediaDialog?.name" width="min(96vw, 900px)" append-to-body destroy-on-close data-testid="media-dialog">
    <template v-if="mediaDialog">
      <video
        v-if="mediaDialog.kind === 'video'"
        :src="mediaDialog.src"
        :poster="mediaDialog.poster"
        controls
        autoplay
        playsinline
        preload="metadata"
        class="block max-h-[70vh] w-full rounded bg-black"
      />
      <audio v-else :src="mediaDialog.src" controls autoplay preload="metadata" class="w-full" />
    </template>
    <template #footer>
      <a v-if="mediaDialog" :href="mediaDialog.download" :download="mediaDialog.name" class="el-button">{{ t('drive.downloadOriginal') }}</a>
    </template>
  </el-dialog>
</template>
