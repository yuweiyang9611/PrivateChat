<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSessionStore } from '@/stores/session'

/**
 * 过了 Cloudflare Access 却进不了应用：邮箱不是家庭成员（没被邀请 / 已停用），或服务器校验不了 Access 身份。
 * 这条路上密码登录用不了，所以不显示登录表单，只说明原因并给出"再试一次 / 换一个邮箱"。
 */
const { t } = useI18n()
const session = useSessionStore()
const denied = computed(() => session.accessDenied)
const notMember = computed(() => denied.value?.reason === 'not-member')

function switchAccount(): void {
  // 结束 Access 会话；下次打开本站时 Cloudflare 会重新让你选邮箱验证
  window.location.assign('/cdn-cgi/access/logout')
}
</script>

<template>
  <div class="flex min-h-dvh items-center justify-center p-4" style="background: var(--pc-bg)" data-testid="access-denied">
    <el-card class="w-full max-w-sm" shadow="hover">
      <template #header>
        <div class="text-center">
          <div class="text-2xl font-semibold" style="color: var(--pc-primary)">{{ t('app.name') }}</div>
          <div class="mt-1 text-sm opacity-70">{{ notMember ? t('access.notMemberTitle') : t('access.invalidTitle') }}</div>
        </div>
      </template>
      <div v-if="denied?.email" class="mb-4 text-center">
        <div class="text-xs opacity-60">{{ t('access.signedInAs') }}</div>
        <div class="mt-1 font-medium break-all" data-testid="access-denied-email">{{ denied.email }}</div>
      </div>
      <p class="mb-5 text-sm leading-relaxed opacity-80">{{ notMember ? t('access.notMemberBody') : t('access.invalidBody') }}</p>
      <el-button type="primary" size="large" class="w-full" data-testid="access-denied-retry" @click="session.load()">
        {{ t('access.retry') }}
      </el-button>
      <el-button size="large" class="mt-3 w-full" style="margin-left: 0" data-testid="access-denied-switch" @click="switchAccount">
        {{ t('access.switchAccount') }}
      </el-button>
    </el-card>
  </div>
</template>
