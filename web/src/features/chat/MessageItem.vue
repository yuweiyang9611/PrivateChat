<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { onLongPress } from '@vueuse/core'
import { ElMessage } from 'element-plus'
import AttachmentView from './AttachmentView.vue'
import type { LocalMessage } from '@/db'
import { useMessagesStore } from '@/stores/messages'
import { useSessionStore } from '@/stores/session'

const props = defineProps<{ message: LocalMessage; mine: boolean }>()
const { t } = useI18n()
const messages = useMessagesStore()
const session = useSessionStore()

/** 家庭场景够用的快捷回应 */
const QUICK = ['👍', '❤️', '😂', '😮', '😢', '🙏', '🎉', '👏']
const RECALL_WINDOW_MS = 2 * 60 * 1000

const bubble = ref<HTMLElement | null>(null)
const open = ref(false)

const sent = computed(() => props.message.status === 'sent' && !props.message.deleted)
const canRecall = computed(() => {
  if (!sent.value) return false
  if (session.user?.role === 'Admin') return true
  return props.mine && Date.now() - new Date(props.message.createdAt).getTime() < RECALL_WINDOW_MS
})
const canCopy = computed(() => props.message.type === 'Text' && !!props.message.body)

/** emoji → { count, mine } */
const grouped = computed(() => {
  const map = new Map<string, { count: number; mine: boolean }>()
  for (const r of props.message.reactions) {
    const g = map.get(r.emoji) ?? { count: 0, mine: false }
    g.count++
    if (r.userId === session.user?.id) g.mine = true
    map.set(r.emoji, g)
  }
  return [...map.entries()].map(([emoji, g]) => ({ emoji, ...g }))
})

function time(iso: string): string {
  return new Date(iso).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
}

// 手机长按、桌面右键或悬停按钮都打开同一个菜单
onLongPress(bubble, () => { if (sent.value) open.value = true }, { delay: 450, modifiers: { prevent: true } })

async function react(emoji: string): Promise<void> {
  open.value = false
  await messages.toggleReaction(props.message.conversationId, props.message.id, emoji)
}

async function copy(): Promise<void> {
  open.value = false
  try {
    await navigator.clipboard.writeText(props.message.body ?? '')
    ElMessage.success(t('chat.copied'))
  } catch {
    ElMessage.warning(t('chat.copyFailed'))
  }
}

async function recall(): Promise<void> {
  open.value = false
  const ok = await messages.recall(props.message.conversationId, props.message.id)
  if (!ok) ElMessage.warning(t('chat.recallFailed'))
}
</script>

<template>
  <div class="group mb-2 flex" :class="mine ? 'justify-end' : 'justify-start'" :data-seq="message.seq">
    <div
      v-if="message.type === 'System' || message.type === 'Call'"
      class="mx-auto my-1 rounded-full px-3 py-1 text-xs"
      style="background: var(--pc-border); color: var(--pc-text-muted)"
    >
      {{ message.body }}
    </div>

    <div v-else class="flex max-w-[75%] flex-col" :class="mine ? 'items-end' : 'items-start'">
      <div class="flex items-center gap-1" :class="mine ? 'flex-row-reverse' : ''">
        <el-popover v-model:visible="open" trigger="click" placement="top" :width="300" :show-arrow="false" popper-class="pc-actions" :teleported="true" :persistent="false" :disabled="!sent">
          <template #reference>
            <div
              ref="bubble"
              class="rounded-2xl text-[15px] leading-relaxed select-none md:select-text"
              :class="message.attachments.length && !message.deleted ? 'p-1' : 'px-3 py-2'"
              :style="mine
                ? 'background: var(--pc-primary); color: #fff; border-bottom-right-radius: 4px'
                : 'background: var(--pc-surface); border: 1px solid var(--pc-border); border-bottom-left-radius: 4px'"
              @contextmenu.prevent="sent && (open = true)"
              @click="open = false"
            >
              <div v-if="message.deleted" class="italic opacity-70">{{ t('chat.recalled') }}</div>
              <AttachmentView v-else-if="message.attachments.length" :message="message" />
              <div v-else class="whitespace-pre-wrap break-words">{{ message.body }}</div>
              <div class="mt-1 text-right text-[11px] opacity-70" :class="message.attachments.length ? 'px-2 pb-1' : ''">
                <span v-if="message.status === 'pending'">{{ t('chat.pending') }}</span>
                <template v-else-if="message.status === 'failed'">
                  <button class="underline" @click.stop="messages.retry(message.conversationId, message.clientMessageId)">{{ t('chat.failed') }}</button>
                  <button v-if="message.attachments.length" class="ml-2 underline" @click.stop="messages.cancel(message.conversationId, message.clientMessageId)">{{ t('chat.cancelUpload') }}</button>
                </template>
                <span v-else>{{ time(message.createdAt) }}</span>
              </div>
            </div>
          </template>

          <!-- 操作菜单：快捷表情 + 复制 / 撤回 -->
          <div class="flex flex-wrap gap-1" data-testid="quick-reactions">
            <button
              v-for="e in QUICK"
              :key="e"
              class="h-9 w-9 rounded-full text-xl transition hover:scale-110"
              :style="grouped.some((g) => g.emoji === e && g.mine) ? 'background: var(--pc-primary-soft)' : ''"
              type="button"
              @click="react(e)"
            >{{ e }}</button>
          </div>
          <div v-if="canCopy || canRecall" class="mt-2 flex gap-2 border-t pt-2" style="border-color: var(--pc-border)">
            <el-button v-if="canCopy" size="small" text @click="copy">{{ t('chat.copy') }}</el-button>
            <el-button v-if="canRecall" size="small" text type="danger" data-testid="recall" @click="recall">{{ t('chat.recall') }}</el-button>
          </div>
        </el-popover>

        <!-- 桌面悬停时露出的菜单按钮 -->
        <button
          v-if="sent"
          class="hidden h-7 w-7 shrink-0 items-center justify-center rounded-full text-base opacity-0 transition group-hover:opacity-100 md:flex"
          style="background: var(--pc-border)"
          :title="t('chat.react')"
          type="button"
          data-testid="msg-actions"
          @click.stop="open = !open"
        >☺</button>
      </div>

      <!-- 回应片：点同一个表情可取消 -->
      <div v-if="grouped.length" class="mt-1 flex flex-wrap gap-1" data-testid="reactions">
        <button
          v-for="g in grouped"
          :key="g.emoji"
          class="flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs"
          :style="g.mine ? 'background: var(--pc-primary-soft); border-color: var(--pc-primary)' : 'background: var(--pc-surface); border-color: var(--pc-border)'"
          type="button"
          @click="react(g.emoji)"
        >
          <span>{{ g.emoji }}</span><span>{{ g.count }}</span>
        </button>
      </div>
    </div>
  </div>
</template>
