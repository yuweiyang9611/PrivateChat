<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import PlaceholderPage from '@/components/PlaceholderPage.vue'

const { t } = useI18n()
</script>

<template>
  <PlaceholderPage :name="t('nav.family')" milestone="M1 实现" />
</template>
