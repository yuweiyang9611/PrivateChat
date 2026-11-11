<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { PhoneFilled, VideoCamera } from '@element-plus/icons-vue'
import { useCallsStore } from '@/stores/calls'
import { useConversationsStore } from '@/stores/conversations'
import { useFamilyStore } from '@/stores/family'

// 来电弹窗：全局挂载，在任何页面都能接。长辈模式下按钮更大（全局字号 +25% 之外再放大）。
const { t } = useI18n()
const calls = useCallsStore()
const conversations = useConversationsStore()
const family = useFamilyStore()

const call = computed(() => calls.incoming!)
const caller = computed(() => family.nameOf(call.value.startedBy))
const conversation = computed(() => conversations.byId(call.value.conversationId ?? ''))
/** 群聊来电带上群名 */
const group = computed(() => (conversation.value && conversation.value.memberIds.length > 2 ? family.titleOf(conversation.value) : ''))
const video = computed(() => call.value.type !== 'Audio')
</script>

<template>
  <div
    class="fixed inset-0 z-[1600] flex items-center justify-center p-4"
    style="background: rgba(27, 23, 20, 0.72)"
    role="alertdialog"
    aria-modal="true"
    data-testid="incoming-call"
    :data-call-id="call.id"
  >
    <div class="flex w-full max-w-sm flex-col items-center gap-2 rounded-3xl px-6 py-8 shadow-2xl" style="background: var(--pc-surface)">
      <el-avatar :size="96" style="background: var(--pc-primary); font-size: 2.25rem">{{ caller.slice(0, 1) }}</el-avatar>
      <div class="mt-2 text-2xl font-semibold" data-testid="incoming-caller">{{ caller }}</div>
      <div style="color: var(--pc-text-muted)">
        {{ t(video ? 'calls.incomingVideo' : 'calls.incomingAudio') }}<template v-if="group"> · {{ group }}</template>
      </div>
      <div v-if="calls.current" class="text-sm" style="color: var(--pc-text-muted)">{{ t('calls.acceptEndsCurrent') }}</div>

      <div class="mt-8 flex w-full items-start justify-around">
        <div class="flex flex-col items-center gap-2">
          <button class="pc-ring-btn" style="background: #e5484d" type="button" data-testid="call-decline" @click="calls.decline()">
            <el-icon :size="30" class="rotate-[135deg]"><PhoneFilled /></el-icon>
          </button>
          <span class="text-sm">{{ t('calls.decline') }}</span>
        </div>
        <div class="flex flex-col items-center gap-2">
          <button class="pc-ring-btn" style="background: #30a46c" type="button" :disabled="calls.entering" data-testid="call-accept" @click="calls.accept()">
            <el-icon :size="30"><component :is="video ? VideoCamera : PhoneFilled" /></el-icon>
          </button>
          <span class="text-sm">{{ t('calls.accept') }}</span>
        </div>
      </div>
    </div>
  </div>
</template>

<style>
.pc-ring-btn {
  display: flex;
  height: 72px;
  width: 72px;
  align-items: center;
  justify-content: center;
  border-radius: 9999px;
  color: #fff;
}
.pc-ring-btn:disabled {
  opacity: 0.5;
}
html.elder .pc-ring-btn {
  height: 92px;
  width: 92px;
}
</style>
