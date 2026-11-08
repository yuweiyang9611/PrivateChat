<script setup lang="ts">
import { useI18n } from 'vue-i18n'

const visible = defineModel<boolean>({ required: true })
const { t } = useI18n()
</script>

<template>
  <el-dialog v-model="visible" :title="t('install.iosTitle')" width="min(92vw, 420px)" append-to-body>
    <ol class="list-decimal space-y-3 pl-5 text-[15px] leading-relaxed">
      <li>{{ t('install.iosStep1') }}</li>
      <li>{{ t('install.iosStep2') }}</li>
      <li>{{ t('install.iosStep3') }}</li>
    </ol>
    <template #footer>
      <el-button type="primary" @click="visible = false">{{ t('common.confirm') }}</el-button>
    </template>
  </el-dialog>
</template>
