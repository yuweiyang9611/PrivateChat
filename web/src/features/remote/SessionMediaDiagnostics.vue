<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { IceCandidateType, PublisherAttemptTelemetry, SessionTelemetryDto, TelemetryStat, ViewerFastAttemptTelemetry } from './telemetry'

const props = defineProps<{ session: SessionTelemetryDto }>()
const { t } = useI18n()
const label = (key: string) => t(`remote.diagnostics.media.${key}`)
const startup = computed(() => props.session.viewer?.startup)
const playback = computed(() => props.session.viewer?.playback)
const publisherAttempts = computed(() => props.session.agent?.publisherAttempts)
type Metric = [labelKey: string, reading: string]

function value(n: number | null | undefined): string {
  return typeof n === 'number' && Number.isFinite(n) ? String(Math.round(n * 10) / 10) : label('unmeasured')
}

function elapsed(ms: number | null | undefined): string {
  if (ms == null || !Number.isFinite(ms)) return label('unmeasured')
  return ms >= 1000 ? `${Math.round(ms / 10) / 100} s` : `${value(ms)} ms`
}

function percentiles(stat: TelemetryStat | null | undefined, second: 'p10' | 'p90' = 'p90'): string {
  return stat && stat.samples > 0 ? `${value(stat.p50)} / ${value(stat[second])}` : label('unmeasured')
}

function candidate(type: IceCandidateType | null): string | null {
  switch (type) {
    case 'host': case 'srflx': case 'prflx': case 'relay': return type
    default: return null
  }
}

function selectedPath(a: PublisherAttemptTelemetry): string {
  const local = candidate(a.localCandidateType)
  const remote = candidate(a.remoteCandidateType)
  if (!local && !remote) return label('unmeasured')
  const turn = local === 'relay' || remote === 'relay' ? 'turnSelected' : local && remote ? 'noTurn' : 'turnUnknown'
  return t('remote.diagnostics.media.pathValue', {
    route: label(turn), local: local ?? '—', remote: remote ?? '—',
  })
}

function outcomeText(outcome: PublisherAttemptTelemetry['outcome'] | ViewerFastAttemptTelemetry['outcome']): string {
  switch (outcome) {
    case 'connected': case 'failed': case 'cancelled': case 'pending': return label(outcome)
    case 'firstFrame': return label('frameReceived')
    default: return label('unknown')
  }
}

function publisherMetrics(a: PublisherAttemptTelemetry): Metric[] {
  return [
    ['iceConfig', elapsed(a.iceConfigMs)], ['gather', elapsed(a.gatherMs)],
    ['publish', elapsed(a.publishMs)], ['handshake', elapsed(a.handshakeMs)],
  ]
}

function viewerMetrics(a: ViewerFastAttemptTelemetry): Metric[] {
  return [['publisherWait', elapsed(a.publisherWaitMs)], ['pullToFirstFrame', elapsed(a.pullToFirstFrameMs)]]
}

const quality = computed<Metric[]>(() => {
  const { agent, viewer } = props.session
  return [
    ['sentFps', percentiles(agent?.sentFps, 'p10')],
    ['decodedFps', percentiles(viewer?.decodedFps, 'p10')],
    ['freezeCount', value(viewer?.freezes)], ['longestFreeze', elapsed(viewer?.longestFreezeMs)],
    ['loss', percentiles(viewer?.lossPermille ?? agent?.lossPermille)],
  ]
})

const fastPlayback = computed<Metric[]>(() => [
  ['decodedFrames', value(playback.value?.fastFramesDecoded)], ['droppedFrames', value(playback.value?.fastFramesDropped)],
  ['nackRequests', value(playback.value?.fastNackRequests)], ['retransmitted', value(playback.value?.fastRetransmittedPackets)],
  ['decodeTime', percentiles(playback.value?.fastDecodeMs)],
])

const relayPlayback = computed<Metric[]>(() => [
  ['decodeQueue', percentiles(playback.value?.relayDecodeQueue)],
  ['skippedUntilKey', value(playback.value?.relaySkippedUntilKey)],
])
</script>

<template>
  <div class="media-diagnostics">
    <section data-testid="diagnostics-startup">
      <h4>{{ label('firstFrame') }}</h4>
      <dl v-if="startup" class="metrics first-frame">
        <div><dt>{{ label('requestToFrame') }}</dt><dd>{{ elapsed(startup.requestToFirstFrameMs) }}</dd></div>
        <div><dt>{{ label('connectToFrame') }}</dt><dd>{{ elapsed(startup.connectToFirstFrameMs) }}</dd></div>
        <div><dt>{{ label('firstLine') }}</dt><dd>{{ startup.firstFrameLine === 'realtime' ? label('fastLine') : startup.firstFrameLine === 'relay' ? label('relayLine') : label('unmeasured') }}</dd></div>
      </dl>
      <p v-else class="muted">{{ label('noStartup') }}</p>
    </section>

    <section v-if="publisherAttempts != null || startup != null" data-testid="diagnostics-attempts">
      <h4>{{ label('attempts') }}</h4>
      <h5>{{ label('publisher') }}</h5>
      <p v-if="!publisherAttempts?.length" class="muted">{{ label(publisherAttempts == null ? 'unreported' : 'noAttempts') }}</p>
      <ol v-else>
        <li v-for="a in publisherAttempts" :key="a.attempt">
          <div class="attempt-heading"><strong>{{ t('remote.diagnostics.media.attempt', { n: value(a.attempt) }) }}</strong><span>{{ outcomeText(a.outcome) }} · {{ elapsed(a.totalMs) }}</span></div>
          <div class="route"><span>{{ label('strategy') }} · {{ label(a.strategy === 'all' || a.strategy === 'relay' ? a.strategy : 'unknown') }}</span><span>{{ label('selectedPath') }} · {{ selectedPath(a) }}</span></div>
          <dl class="metrics"><div v-for="[key, reading] in publisherMetrics(a)" :key="key"><dt>{{ label(key) }}</dt><dd>{{ reading }}</dd></div></dl>
        </li>
      </ol>
      <h5>{{ label('viewer') }}</h5>
      <p v-if="!startup?.attempts.length" class="muted">{{ label(startup == null ? 'unreported' : 'noAttempts') }}</p>
      <ol v-else>
        <li v-for="a in startup.attempts" :key="a.attempt">
          <div class="attempt-heading"><strong>{{ t('remote.diagnostics.media.attempt', { n: value(a.attempt) }) }}</strong><span>{{ outcomeText(a.outcome) }} · {{ elapsed(a.totalMs) }}</span></div>
          <dl class="metrics"><div v-for="[key, reading] in viewerMetrics(a)" :key="key"><dt>{{ label(key) }}</dt><dd>{{ reading }}</dd></div></dl>
        </li>
      </ol>
    </section>

    <section data-testid="diagnostics-playback">
      <h4>{{ label('playback') }}</h4>
      <dl class="metrics"><div v-for="[key, reading] in quality" :key="key"><dt>{{ label(key) }}</dt><dd>{{ reading }}</dd></div></dl>
      <template v-if="playback">
        <h5>{{ label('fastLine') }}</h5>
        <dl class="metrics"><div v-for="[key, reading] in fastPlayback" :key="key"><dt>{{ label(key) }}</dt><dd>{{ reading }}</dd></div></dl>
        <h5>{{ label('relayLine') }}</h5>
        <dl class="metrics"><div v-for="[key, reading] in relayPlayback" :key="key"><dt>{{ label(key) }}</dt><dd>{{ reading }}</dd></div></dl>
      </template>
      <p v-else class="muted">{{ label('noPlayback') }}</p>
    </section>
  </div>
</template>

<style scoped>
.media-diagnostics { display: grid; gap: 1rem; font-size: 0.8125rem; line-height: 1.5; }
h4, h5, strong { font-weight: 600; }
h4 { margin: 0 0 0.4rem; font-size: 0.9375rem; }
h5 { margin: 0.65rem 0 0.25rem; font-size: inherit; }
.metrics { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 9rem), 1fr)); gap: 0.35rem 1rem; margin: 0; }
.metrics dt, .muted { color: var(--pc-text-muted); }
.metrics dd { margin: 0; font-variant-numeric: tabular-nums; }
.first-frame dd { font-size: 0.9375rem; font-weight: 600; }
.muted { margin: 0.25rem 0; }
ol { list-style: none; margin: 0; padding: 0; }
li { border-top: 1px solid var(--el-border-color-lighter); padding: 0.4rem 0; }
.attempt-heading, .route { display: flex; flex-wrap: wrap; gap: 0.15rem 0.8rem; }
.attempt-heading { margin-bottom: 0.25rem; }
.route { margin-bottom: 0.25rem; }
</style>
