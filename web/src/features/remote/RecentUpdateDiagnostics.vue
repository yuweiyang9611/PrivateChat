<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { formatWhen } from './format'
import type { RecentUpdateDto } from './telemetry'

defineProps<{ update?: RecentUpdateDto | null }>()
const { t } = useI18n()
</script>

<template>
  <section data-testid="diagnostics-recent-update">
    <h3 class="mb-2 text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('remote.diagnostics.update.title') }}</h3>
    <p v-if="!update" class="text-sm" style="color: var(--pc-text-muted)">{{ t('remote.diagnostics.update.missing') }}</p>
    <template v-else>
      <p class="mb-2 text-sm font-semibold">
        {{ update.state === 'restarting' && update.confirmedAt ? t('remote.diagnostics.update.confirmed') : t(`remote.diagnostics.update.states.${update.state}`) }}
      </p>
      <dl class="update-metrics">
        <div><dt>{{ t('remote.diagnostics.update.source') }}</dt><dd>{{ t(`remote.diagnostics.update.${update.source}`) }}</dd></div>
        <div><dt>{{ t('remote.diagnostics.update.checkedVersion') }}</dt><dd>v{{ update.currentVersion }}</dd></div>
        <div><dt>{{ t('remote.diagnostics.update.target') }}</dt><dd>{{ update.targetVersion ? 'v' + update.targetVersion : '—' }}</dd></div>
        <div><dt>{{ t('remote.diagnostics.update.elapsed') }}</dt><dd>{{ update.elapsedMs < 1000 ? update.elapsedMs + ' ms' : (Math.round(update.elapsedMs / 10) / 100) + ' s' }}</dd></div>
        <div><dt>{{ t('remote.diagnostics.update.checkedAt') }}</dt><dd>{{ formatWhen(update.checkedAt) }}</dd></div>
        <div><dt>{{ t('remote.diagnostics.update.receivedAt') }}</dt><dd>{{ formatWhen(update.receivedAt) }}</dd></div>
        <div v-if="update.state === 'restarting' && update.confirmedAt"><dt>{{ t('remote.diagnostics.update.confirmedAt') }}</dt><dd>{{ formatWhen(update.confirmedAt) }}</dd></div>
        <div v-if="update.failureStage"><dt>{{ t('remote.diagnostics.update.failureStage') }}</dt><dd>{{ t(`remote.diagnostics.update.stages.${update.failureStage}`) }}</dd></div>
        <div v-if="update.errorCode"><dt>{{ t('remote.diagnostics.update.error') }}</dt><dd>{{ t(`remote.diagnostics.update.errors.${update.errorCode}`) }}</dd></div>
      </dl>
    </template>
  </section>
</template>

<style scoped>
.update-metrics { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 9rem), 1fr)); gap: .5rem 1rem; font-size: .8125rem; }
.update-metrics dt { color: var(--pc-text-muted); }
.update-metrics dd { margin: .1rem 0 0; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
</style>
