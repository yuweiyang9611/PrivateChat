<script setup lang="ts">
import { onBeforeUnmount, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import CallOverlay from './CallOverlay.vue'
import IncomingCallDialog from './IncomingCallDialog.vue'
import { useCallsStore } from '@/stores/calls'
import { useFamilyStore } from '@/stores/family'

// 通话的全局宿主：来电弹窗、通话界面、提示语，以及"从通知点进来"的两条入口（地址里的 ?call=、Service Worker 的消息）。
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const calls = useCallsStore()
const family = useFamilyStore()

watch(
  () => calls.notice,
  (n) => {
    if (!n) return
    const name = n.params?.userId ? family.nameOf(n.params.userId) : ''
    ElMessage({ type: n.type, message: t(n.key, { name }), duration: n.type === 'error' ? 5000 : 3000 })
  },
)

// 点来电通知打开的页面：/messages/{会话}?call={通话}[&action=answer]
watch(
  () => route.query.call,
  (id) => {
    if (typeof id !== 'string' || !id) return
    const action = typeof route.query.action === 'string' ? route.query.action : undefined
    const { call: _call, action: _action, ...rest } = route.query
    void router.replace({ path: route.path, query: rest })
    void calls.openFromLink(id, action)
  },
  { immediate: true },
)

// 页面本来就开着时，Service Worker 把通知上点的按钮转过来（不重新加载页面）
function onWorkerMessage(e: MessageEvent): void {
  const data = e.data as { type?: string; callId?: string; action?: string } | null
  if (data?.type === 'call-action' && data.callId) void calls.openFromLink(data.callId, data.action)
}
onMounted(() => navigator.serviceWorker?.addEventListener('message', onWorkerMessage))
onBeforeUnmount(() => navigator.serviceWorker?.removeEventListener('message', onWorkerMessage))
</script>

<template>
  <CallOverlay v-if="calls.current" />
  <IncomingCallDialog v-if="calls.incoming" />
</template>
