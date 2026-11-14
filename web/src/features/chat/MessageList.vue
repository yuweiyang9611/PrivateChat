<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch, type ComponentPublicInstance } from 'vue'
import { useI18n } from 'vue-i18n'
import { useVirtualizer } from '@tanstack/vue-virtual'
import { ArrowDown } from '@element-plus/icons-vue'
import MessageItem from './MessageItem.vue'
import { parseMeta, type LocalMessage } from '@/db'
import { useMessagesStore } from '@/stores/messages'
import { useSessionStore } from '@/stores/session'

/** focusSeq：滚到这条消息并闪一下（搜索结果跳过来）；处理完发 focused，父组件把它清掉，下次同一条还能再跳 */
const props = defineProps<{ conversationId: string; focusSeq?: number | null }>()
const emit = defineEmits<{ focused: [found: boolean] }>()
const { t } = useI18n()
const messages = useMessagesStore()
const session = useSessionStore()

const scroller = ref<HTMLElement | null>(null)
const list = computed(() => messages.list(props.conversationId))
const hasMore = computed(() => messages.hasMore[props.conversationId] !== false)
const loadingOlder = computed(() => !!messages.loadingOlder[props.conversationId])

/** 没测量前的估算高度：文字按字数折行，图片按比例，其它固定；误差由 ResizeObserver 测量后校正 */
function estimate(m: LocalMessage): number {
  let h: number
  const att = m.attachments[0]
  if (m.deleted || m.type === 'System' || m.type === 'Call') h = 40
  else if (att?.kind === 'Image') {
    const meta = parseMeta(att.meta)
    const w = meta.width ?? 4, hh = meta.height ?? 3
    h = Math.max(60, Math.round(hh * Math.min(260 / w, 320 / hh, 1))) + 36
  } else if (att?.kind === 'Video') h = 220
  else if (att?.kind === 'Audio') h = 72
  else if (att) h = 80
  else h = 44 + Math.ceil((m.body?.length ?? 1) / 28) * 24
  if (m.reactions.length) h += 30
  return h + 8 // mb-2
}

const virtualizer = useVirtualizer(
  computed(() => ({
    count: list.value.length,
    getScrollElement: () => scroller.value,
    estimateSize: (i: number) => estimate(list.value[i]!),
    getItemKey: (i: number) => list.value[i]!.clientMessageId || list.value[i]!.id,
    overscan: 6,
    paddingStart: 12,
    paddingEnd: 12,
  })),
)
const items = computed(() => virtualizer.value.getVirtualItems())
/** 模板 ref 的签名比 measureElement 宽，收窄一下 */
function measure(el: Element | ComponentPublicInstance | null): void {
  virtualizer.value.measureElement(el instanceof Element ? el : null)
}
const totalSize = computed(() => virtualizer.value.getTotalSize())

// ---- 底部吸附 ----
const STICK_THRESHOLD = 80
const stickToBottom = ref(true)
const unseen = ref(0)

function distanceToBottom(): number {
  const el = scroller.value
  return el ? el.scrollHeight - el.scrollTop - el.clientHeight : 0
}

/** 一律经 virtualizer 滚动：它记着目标位置，测量后会自动修正；直接改 scrollTop 会被它当成异常事件忽略 */
function scrollToBottom(behavior: ScrollBehavior = 'auto'): void {
  const n = list.value.length
  if (!scroller.value || n === 0) return
  virtualizer.value.scrollToIndex(n - 1, { align: 'end', behavior: behavior === 'smooth' ? 'smooth' : 'auto' })
}

function jumpToLatest(): void {
  stickToBottom.value = true
  unseen.value = 0
  scrollToBottom('smooth')
  void messages.read(props.conversationId)
}

// ---- 向上翻页：记住总高度差，翻页后把 scrollTop 补回去，看起来位置不动 ----
const TOP_THRESHOLD = 240
async function maybeLoadOlder(): Promise<void> {
  const el = scroller.value
  if (!el || !hasMore.value || loadingOlder.value || el.scrollTop > TOP_THRESHOLD) return
  const prevTotal = totalSize.value
  const prevTop = el.scrollTop
  const n = await messages.loadOlder(props.conversationId)
  if (!n) return
  await nextTick()
  const delta = totalSize.value - prevTotal
  virtualizer.value.scrollToOffset(prevTop + delta)
  // 新插入的项测量后高度有变，virtual-core 默认会对视口上方的尺寸变化做补偿
}

function onScroll(): void {
  const near = distanceToBottom() < STICK_THRESHOLD
  if (near !== stickToBottom.value) {
    stickToBottom.value = near
    if (near) {
      unseen.value = 0
      void messages.read(props.conversationId)
    }
  }
  void maybeLoadOlder()
}

// 新消息（末尾那条变了）：自己发的或本来就在底部 → 跟到底；否则计数提示。
// 往前翻页、补空档只是在中间 / 前面插入，末尾不变，不算新消息
const keyOf = (m: LocalMessage) => m.clientMessageId || m.id
const lastKey = computed(() => {
  const l = list.value
  return l.length ? keyOf(l[l.length - 1]!) : null
})
watch(lastKey, async (key, oldKey) => {
  if (key === null) return
  const l = list.value
  const mine = l[l.length - 1]!.senderId === session.user?.id
  if (stickToBottom.value || mine || oldKey === null || oldKey === undefined) {
    stickToBottom.value = true
    await nextTick()
    scrollToBottom()
  } else {
    const before = l.findIndex((m) => keyOf(m) === oldKey)
    unseen.value += before >= 0 ? l.length - 1 - before : 1
  }
})

// ---- 空档：相邻两条已发送的消息 seq 不连号（跳到很早的消息时只取了它附近一段）----
const filling = ref<number | null>(null)
/** 补过、发现其实没有缺的空档（按较新那条的 seq 记） */
const noGap = ref(new Set<number>())

function gapAbove(i: number): { older: number; newer: number } | null {
  if (i <= 0) return null
  const cur = list.value[i]!
  const prev = list.value[i - 1]!
  if (cur.status !== 'sent' || prev.status !== 'sent' || cur.seq - prev.seq <= 1 || noGap.value.has(cur.seq)) return null
  return { older: prev.seq, newer: cur.seq }
}

async function fillGap(gap: { older: number; newer: number }): Promise<void> {
  if (filling.value !== null) return
  filling.value = gap.newer
  try {
    const n = await messages.fillGap(props.conversationId, gap.older, gap.newer)
    if (n === 0) noGap.value = new Set([...noGap.value, gap.newer])
  } finally {
    filling.value = null
  }
}

// 测量导致总高度变化（图片加载、首屏估算校正）时，吸附状态下继续贴底
watch(totalSize, async () => {
  if (!stickToBottom.value) return
  await nextTick()
  if (distanceToBottom() > 1) scrollToBottom()
})

// 切换会话：回到底部、清状态
watch(
  () => props.conversationId,
  async () => {
    stickToBottom.value = true
    unseen.value = 0
    await nextTick()
    scrollToBottom()
  },
)

const flashSeq = ref<number | null>(null)
let flashTimer: number | undefined

watch(
  () => props.focusSeq,
  async (seq) => {
    if (seq === null || seq === undefined) return
    await nextTick()
    const index = list.value.findIndex((m) => m.seq === seq && m.status === 'sent')
    emit('focused', index >= 0)
    if (index < 0) return
    // 不再贴底：新消息、图片加载都不会把视图拉回最下面
    stickToBottom.value = false
    virtualizer.value.scrollToIndex(index, { align: 'center' })
    // 估算高度被实际测量校正后位置会偏，等两帧再对一次
    requestAnimationFrame(() => requestAnimationFrame(() => {
      const again = list.value.findIndex((m) => m.seq === seq && m.status === 'sent')
      if (again >= 0) virtualizer.value.scrollToIndex(again, { align: 'center' })
    }))
    flashSeq.value = seq
    window.clearTimeout(flashTimer)
    flashTimer = window.setTimeout(() => (flashSeq.value = null), 2600)
  },
  { immediate: true },
)

onMounted(async () => {
  await nextTick()
  if (props.focusSeq === null || props.focusSeq === undefined) scrollToBottom()
  // 首屏内容不足一屏时主动补一页
  if (scroller.value && scroller.value.scrollHeight <= scroller.value.clientHeight) void maybeLoadOlder()
})
</script>

<template>
  <div class="relative min-h-0">
    <div ref="scroller" class="h-full overflow-y-auto px-4" data-testid="message-scroller" @scroll.passive="onScroll">
      <div v-if="loadingOlder" class="py-2 text-center text-xs" style="color: var(--pc-text-muted)">{{ t('chat.loadingOlder') }}</div>
      <div v-else-if="!hasMore && list.length" class="py-2 text-center text-xs" style="color: var(--pc-text-muted)">{{ t('chat.noMore') }}</div>
      <div :style="{ height: totalSize + 'px', position: 'relative', width: '100%' }">
        <div
          v-for="row in items"
          :key="row.key as string"
          :ref="measure"
          :data-index="row.index"
          :style="{ position: 'absolute', top: 0, left: 0, width: '100%', transform: `translateY(${row.start}px)` }"
        >
          <div v-if="gapAbove(row.index)" class="mb-2 flex justify-center">
            <button
              type="button"
              class="rounded-full px-3 py-1 text-xs"
              style="background: var(--pc-border); color: var(--pc-text-muted)"
              data-testid="message-gap"
              :data-newer="list[row.index]!.seq"
              :data-older="list[row.index - 1]!.seq"
              :disabled="filling !== null"
              @click="fillGap(gapAbove(row.index)!)"
            >
              {{ filling === list[row.index]!.seq ? t('chat.gapLoading') : t('chat.gapLoad') }}
            </button>
          </div>
          <MessageItem
            :message="list[row.index]!"
            :mine="list[row.index]!.senderId === session.user?.id"
            :highlighted="flashSeq !== null && list[row.index]!.seq === flashSeq && list[row.index]!.status === 'sent'"
          />
        </div>
      </div>
    </div>

    <transition name="el-fade-in">
      <button
        v-if="unseen > 0"
        class="absolute bottom-3 left-1/2 flex -translate-x-1/2 items-center gap-1 rounded-full px-3 py-1.5 text-sm shadow"
        style="background: var(--pc-primary); color: #fff"
        type="button"
        data-testid="jump-latest"
        @click="jumpToLatest"
      >
        <el-icon><ArrowDown /></el-icon>{{ t('chat.newMessages', { n: unseen }) }}
      </button>
    </transition>
  </div>
</template>
