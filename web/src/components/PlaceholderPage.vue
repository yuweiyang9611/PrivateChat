<script setup lang="ts">
import { useI18n } from 'vue-i18n'

defineProps<{ name: string; milestone?: string }>()
const { t } = useI18n()
</script>

<template>
  <div class="flex h-full flex-col items-center justify-center gap-2 p-8 text-center">
    <h1 class="text-2xl font-semibold">{{ name }}</h1>
    <el-tag type="warning" effect="plain">{{ milestone ?? t('placeholder.note') }}</el-tag>
    <p style="color: var(--pc-text-muted)">{{ t('placeholder.desc') }}</p>
  </div>
</template>
