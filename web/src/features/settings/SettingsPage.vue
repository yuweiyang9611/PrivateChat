<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useUiStore } from '@/stores/ui'
import { useSessionStore } from '@/stores/session'
import { disablePush, enablePush, pushState, sendTestPush } from '@/push'

const { t } = useI18n()
const ui = useUiStore()
const session = useSessionStore()

const pushOn = computed(() => pushState.value === 'subscribed')
const pushDisabled = computed(() => pushState.value === 'unsupported' || pushState.value === 'denied')
const pushHint = computed(() => {
  if (pushState.value === 'unsupported') return t('push.unsupported')
  if (pushState.value === 'denied') return t('push.denied')
  return t('push.desc')
})
const busy = ref(false)

async function togglePush(on: string | number | boolean): Promise<void> {
  busy.value = true
  try {
    if (on) await enablePush()
    else await disablePush()
  } finally {
    busy.value = false
  }
}

async function test(): Promise<void> {
  const ok = await sendTestPush()
  ElMessage(ok ? { type: 'success', message: t('push.testSent') } : { type: 'warning', message: t('push.testNone') })
}
</script>

<template>
  <div class="mx-auto max-w-xl p-4">
    <h1 class="mb-4 text-xl font-semibold">{{ t('nav.settings') }}</h1>

    <el-card shadow="never" class="mb-4">
      <div class="flex items-center gap-3">
        <el-avatar :size="56" style="background: var(--pc-primary)">{{ session.user?.displayName?.slice(0, 1) ?? '?' }}</el-avatar>
        <div>
          <div class="font-medium">{{ session.user?.displayName ?? '—' }}</div>
          <div class="text-sm" style="color: var(--pc-text-muted)">{{ session.user?.email ?? t('session.offline') }}</div>
        </div>
      </div>
    </el-card>

    <el-card shadow="never" class="mb-4">
      <div class="flex items-center justify-between py-2">
        <div class="min-w-0 pr-3">
          <div>{{ t('push.title') }}</div>
          <div class="text-xs" style="color: var(--pc-text-muted)">{{ pushHint }}</div>
        </div>
        <el-switch :model-value="pushOn" :disabled="pushDisabled" :loading="busy" data-testid="push-switch" @change="togglePush" />
      </div>
      <div v-if="pushOn" class="pt-1">
        <el-button size="small" data-testid="push-test" @click="test">{{ t('push.test') }}</el-button>
      </div>
    </el-card>

    <el-card shadow="never">
      <div class="flex items-center justify-between py-2">
        <div>
          <div>{{ t('settings.elderMode') }}</div>
          <div class="text-xs" style="color: var(--pc-text-muted)">{{ t('settings.elderModeDesc') }}</div>
        </div>
        <el-switch v-model="ui.elderMode" />
      </div>
      <el-divider class="!my-1" />
      <div class="flex items-center justify-between py-2">
        <div>{{ t('settings.darkMode') }}</div>
        <el-switch v-model="ui.isDark" />
      </div>
      <el-divider class="!my-1" />
      <router-link to="/devices" class="flex items-center justify-between py-2">
        <span>{{ t('settings.devices') }}</span><span style="color: var(--pc-text-muted)">›</span>
      </router-link>
      <router-link to="/family" class="flex items-center justify-between py-2">
        <span>{{ t('nav.family') }}</span><span style="color: var(--pc-text-muted)">›</span>
      </router-link>
    </el-card>
  </div>
</template>
