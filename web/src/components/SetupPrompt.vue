<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bell, Close, Download } from '@element-plus/icons-vue'
import IosInstallGuide from './IosInstallGuide.vue'
import { dismissPush, enablePush, pushDismissed, pushState } from '@/push'
import { installEvent, isIos, isStandalone, promptInstall } from '@/pwa/install'

/**
 * 会话列表顶部的一次性引导条，同一时间只出一条：
 * 1. 能直接安装（安卓 / 桌面 Chrome、Edge）→ 安装到主屏幕；
 * 2. iPhone 还没添加到主屏幕 → 教怎么添加（iOS 只有装了才能收推送）；
 * 3. 已具备推送条件但还没问过 → 开启通知。
 * 每条关掉后不再出现（换账号会重置）。
 */
const { t } = useI18n()
const INSTALL_KEY = 'pc.install.dismissed'

function readFlag(key: string): boolean {
  try {
    return localStorage.getItem(key) === '1'
  } catch {
    return false
  }
}
function writeFlag(key: string): void {
  try {
    localStorage.setItem(key, '1')
  } catch {
    // 隐私模式
  }
}

const installHidden = ref(readFlag(INSTALL_KEY))
const pushHidden = ref(pushDismissed())
const busy = ref(false)
const guide = ref(false)

/** 局域网 http 直连不是安全上下文：装了也收不到推送、SW 也不工作，不引导安装 */
const secure = typeof window !== 'undefined' && window.isSecureContext

const mode = computed<'install' | 'ios' | 'push' | null>(() => {
  if (secure && !installHidden.value && !isStandalone.value) {
    if (installEvent.value) return 'install'
    if (isIos) return 'ios'
  }
  if (!pushHidden.value && pushState.value === 'default') return 'push'
  return null
})

async function act(): Promise<void> {
  // 先记下当前是哪一条：await 之后 mode 可能已经切到下一条
  const current = mode.value
  busy.value = true
  try {
    if (current === 'install') {
      // 不管同意与否，这个安装事件都用掉了；拒绝的话不再打扰
      if (!(await promptInstall())) hide(current)
    } else if (current === 'ios') {
      guide.value = true
    } else if (current === 'push') {
      if (!(await enablePush())) hide(current)
    }
  } finally {
    busy.value = false
  }
}

function hide(which = mode.value): void {
  if (which === 'push') {
    pushHidden.value = true
    dismissPush()
  } else {
    installHidden.value = true
    writeFlag(INSTALL_KEY)
  }
}
</script>

<template>
  <div
    v-if="mode"
    class="mx-3 mb-2 flex items-center gap-2 rounded-lg px-3 py-2 text-sm"
    style="background: var(--pc-primary-soft); color: var(--pc-text)"
    :data-testid="`setup-prompt-${mode}`"
  >
    <el-icon :size="18" style="color: var(--pc-primary)"><Bell v-if="mode === 'push'" /><Download v-else /></el-icon>
    <span class="flex-1">{{ mode === 'push' ? t('push.prompt') : mode === 'ios' ? t('install.iosPrompt') : t('install.prompt') }}</span>
    <el-button size="small" type="primary" :loading="busy" @click="act">
      {{ mode === 'push' ? t('push.enable') : mode === 'ios' ? t('install.iosAction') : t('install.action') }}
    </el-button>
    <el-button size="small" text :icon="Close" :aria-label="t('common.cancel')" @click="hide()" />
    <IosInstallGuide v-model="guide" />
  </div>
</template>
