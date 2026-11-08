<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useUiStore } from '@/stores/ui'
import { useSessionStore } from '@/stores/session'
import { disablePush, enablePush, pushState, sendTestPush } from '@/push'
import { installEvent, isIos, isStandalone, promptInstall } from '@/pwa/install'
import { online } from '@/net/online'
import IosInstallGuide from '@/components/IosInstallGuide.vue'

const { t } = useI18n()
const ui = useUiStore()
const session = useSessionStore()

const pushOn = computed(() => pushState.value === 'subscribed')
const pushDisabled = computed(() => pushState.value === 'unsupported' || pushState.value === 'denied')
const pushHint = computed(() => {
  if (pushState.value === 'unsupported') return secure && isIos && !isStandalone.value ? t('install.iosPrompt') : t('push.unsupported')
  if (pushState.value === 'denied') return t('push.denied')
  return t('push.desc')
})
const busy = ref(false)

async function togglePush(on: string | number | boolean): Promise<void> {
  if (!online.value) {
    ElMessage.warning(t('chat.offlineAction'))
    return
  }
  busy.value = true
  try {
    if (on) await enablePush()
    else await disablePush()
  } catch {
    ElMessage.warning(t('chat.offlineAction'))
  } finally {
    busy.value = false
  }
}

async function test(): Promise<void> {
  let ok = false
  try {
    ok = await sendTestPush()
  } catch {
    ok = false
  }
  ElMessage(ok ? { type: 'success', message: t('push.testSent') } : { type: 'warning', message: t('push.testNone') })
}

// ---- 安装 ----
const guide = ref(false)
/** 安装、推送都要求安全上下文；局域网 http://IP:8080 直连时只能看提示 */
const secure = typeof window !== 'undefined' && window.isSecureContext
const installHint = computed(() => {
  if (isStandalone.value) return t('install.installed')
  if (!secure) return t('install.needsHttps')
  if (installEvent.value) return t('install.desc')
  if (isIos) return t('install.iosPrompt')
  return t('install.browserMenu')
})

async function install(): Promise<void> {
  if (installEvent.value) await promptInstall()
  else if (isIos) guide.value = true
}

// ---- 退出登录 ----
const loggingOut = ref(false)
async function logout(): Promise<void> {
  try {
    await ElMessageBox.confirm(t('session.logoutConfirm'), t('session.logout'), {
      type: 'warning',
      confirmButtonText: t('session.logout'),
      cancelButtonText: t('common.cancel'),
    })
  } catch {
    return
  }
  loggingOut.value = true
  const result = await session.logout()
  // 成功时页面会跳走；失败时留在这里并说明原因
  loggingOut.value = false
  if (result === 'offline') ElMessage.warning(t('session.logoutOffline'))
  else if (result === 'failed') ElMessage.error(t('session.logoutFailed'))
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
      <el-divider class="!my-1" />
      <div class="flex items-center justify-between py-2">
        <div class="min-w-0 pr-3">
          <div>{{ t('install.title') }}</div>
          <div class="text-xs" style="color: var(--pc-text-muted)">{{ installHint }}</div>
        </div>
        <el-button
          v-if="secure && !isStandalone && (installEvent || isIos)"
          size="small"
          type="primary"
          plain
          data-testid="install-button"
          @click="install"
        >
          {{ isIos && !installEvent ? t('install.iosAction') : t('install.action') }}
        </el-button>
      </div>
    </el-card>

    <el-card shadow="never" class="mb-4">
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

    <el-button class="w-full" type="danger" plain :loading="loggingOut" data-testid="logout" @click="logout">{{ t('session.logout') }}</el-button>

    <IosInstallGuide v-model="guide" />
  </div>
</template>
