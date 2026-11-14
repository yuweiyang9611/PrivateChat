<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { ArrowLeft, Phone, VideoCamera, MoreFilled } from '@element-plus/icons-vue'
import MessageList from './MessageList.vue'
import Composer from './Composer.vue'
import { useConversationsStore } from '@/stores/conversations'
import { useMessagesStore } from '@/stores/messages'
import { useCallsStore } from '@/stores/calls'
import { useFamilyStore } from '@/stores/family'
import { callsSupported } from '@/features/calls/rtc/media'
import { typing } from '@/realtime/connection'

const props = defineProps<{ id: string }>()
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const conversations = useConversationsStore()
const messages = useMessagesStore()
const calls = useCallsStore()
const family = useFamilyStore()

const conversation = computed(() => conversations.byId(props.id))
const typingUsers = computed(() => typing.value[props.id] ?? [])

// ---- 通话入口 ----
const canCall = computed(() => callsSupported() && calls.enabled && !calls.busy)
const callHint = computed(() => (!callsSupported() ? t('calls.unsupported') : !calls.enabled ? t('calls.notConfigured') : ''))
/** 这个会话里正在进行、我还没进去的通话 */
const ongoing = computed(() => {
  const c = calls.ongoingIn(props.id)
  return c && calls.current?.callId !== c.id ? c : null
})
const ongoingText = computed(() => {
  const c = ongoing.value
  if (!c) return ''
  const names = c.participants.map((p) => family.nameOf(p.userId)).join('、')
  return names ? t('calls.ongoingWith', { names }) : t('calls.ringing')
})

/** 已经加载好的会话（跳到某条消息要等加载完） */
const loadedId = ref<string | null>(null)
const focusSeq = ref<number | null>(null)
/** 每次跳转一个编号；页面卸载或开始新的跳转后，旧的那次回来就什么都不做 */
let jump = 0
onBeforeUnmount(() => jump++)

watch(
  () => props.id,
  async (id) => {
    loadedId.value = null
    await messages.load(id)
    if (props.id !== id) return
    loadedId.value = id
    await messages.read(id)
  },
  { immediate: true },
)

// 搜索结果跳过来：/messages/{id}?seq=N —— 往前翻到那条消息，滚过去闪一下，然后把参数去掉
watch(
  [() => route.query.seq, loadedId],
  async ([raw, loaded]) => {
    const seq = Number(raw)
    if (typeof raw !== 'string' || !Number.isSafeInteger(seq) || seq <= 0 || loaded !== props.id) return
    const my = ++jump
    const path = route.path
    const result = await messages.ensureSeq(loaded, seq)
    if (my !== jump || loadedId.value !== loaded || route.path !== path || route.query.seq !== raw) return
    if (result === 'found') focusSeq.value = seq
    else ElMessage.warning(t(result === 'missing' ? 'search.messageGone' : 'search.jumpFailed'))
    const { seq: _drop, ...rest } = route.query
    void router.replace({ path, query: rest })
  },
  { immediate: true },
)

function onFocused(found: boolean): void {
  focusSeq.value = null
  if (!found) ElMessage.warning(t('search.messageGone'))
}
</script>

<template>
  <div class="flex h-full flex-col">
    <header class="flex items-center gap-2 border-b px-3 py-2" style="border-color: var(--pc-border); background: var(--pc-surface)">
      <router-link to="/messages" class="touch-target flex items-center justify-center lg:hidden">
        <el-icon :size="22"><ArrowLeft /></el-icon>
      </router-link>
      <div class="min-w-0 flex-1">
        <div class="truncate font-semibold" data-testid="chat-title">{{ family.titleOf(conversation) || '…' }}</div>
        <div class="text-xs" style="color: var(--pc-text-muted)">
          <span v-if="typingUsers.length">正在输入…</span>
          <span v-else-if="conversation">{{ t('chat.members', { n: conversation.memberIds.length }) }}</span>
        </div>
      </div>
      <el-button :icon="Phone" circle text :disabled="!canCall" :title="callHint || t('calls.audioCall')" data-testid="chat-call-audio" @click="calls.startCall(id, 'Audio')" />
      <el-button :icon="VideoCamera" circle text :disabled="!canCall" :title="callHint || t('calls.videoCall')" data-testid="chat-call-video" @click="calls.startCall(id, 'Video')" />
      <el-button :icon="MoreFilled" circle text />
    </header>

    <!-- 会话里有通话正在进行：随时可以加入 -->
    <div v-if="ongoing" class="flex items-center gap-3 px-4 py-2 text-sm" style="background: #e6f6ec; color: #18794e" data-testid="chat-call-banner">
      <el-icon :size="18"><component :is="ongoing.type === 'Audio' ? Phone : VideoCamera" /></el-icon>
      <span class="min-w-0 flex-1 truncate">{{ ongoingText }}</span>
      <el-button type="success" size="small" round :disabled="!canCall" data-testid="chat-call-join" @click="calls.join(ongoing.id)">{{ t('calls.joinCall') }}</el-button>
    </div>

    <MessageList :conversation-id="id" :focus-seq="focusSeq" class="min-h-0 flex-1" @focused="onFocused" />

    <Composer :conversation-id="id" />
  </div>
</template>
