<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useUiStore } from '@/stores/ui'
import { useSessionStore } from '@/stores/session'

const { t } = useI18n()
const ui = useUiStore()
const session = useSessionStore()
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
