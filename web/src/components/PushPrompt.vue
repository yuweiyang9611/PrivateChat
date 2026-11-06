<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bell, Close } from '@element-plus/icons-vue'
import { dismissPush, enablePush, pushDismissed, pushState } from '@/push'

const { t } = useI18n()
const hidden = ref(pushDismissed())
const busy = ref(false)

/** 只在「支持、还没问过、用户没拒绝过」时露出 */
const visible = computed(() => !hidden.value && pushState.value === 'default')

async function enable(): Promise<void> {
  busy.value = true
  try {
    const ok = await enablePush()
    if (!ok) hide()
  } finally {
    busy.value = false
  }
}

function hide(): void {
  hidden.value = true
  dismissPush()
}
</script>

<template>
  <div
    v-if="visible"
    class="mx-3 mb-2 flex items-center gap-2 rounded-lg px-3 py-2 text-sm"
    style="background: var(--pc-primary-soft); color: var(--pc-text)"
    data-testid="push-prompt"
  >
    <el-icon :size="18" style="color: var(--pc-primary)"><Bell /></el-icon>
    <span class="flex-1">{{ t('push.prompt') }}</span>
    <el-button size="small" type="primary" :loading="busy" @click="enable">{{ t('push.enable') }}</el-button>
    <el-button size="small" text :icon="Close" @click="hide" />
  </div>
</template>
