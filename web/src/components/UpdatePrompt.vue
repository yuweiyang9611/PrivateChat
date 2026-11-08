<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Refresh } from '@element-plus/icons-vue'
import { applyUpdate, needRefresh } from '@/pwa/update'

/** 新版本已下载：底部浮条，用户点了才切换（不在录音、上传时突然刷新） */
const { t } = useI18n()
const busy = ref(false)

async function update(): Promise<void> {
  busy.value = true
  await applyUpdate()
}
</script>

<template>
  <transition name="el-fade-in">
    <div
      v-if="needRefresh"
      class="fixed left-1/2 z-[3000] flex -translate-x-1/2 items-center gap-3 rounded-full px-4 py-2 text-sm shadow-lg"
      style="bottom: calc(76px + env(safe-area-inset-bottom)); background: var(--pc-surface); border: 1px solid var(--pc-border); color: var(--pc-text)"
      role="status"
      data-testid="update-prompt"
    >
      <span>{{ t('session.updateReady') }}</span>
      <el-button size="small" type="primary" round :icon="Refresh" :loading="busy" @click="update">{{ t('session.updateNow') }}</el-button>
    </div>
  </transition>
</template>
