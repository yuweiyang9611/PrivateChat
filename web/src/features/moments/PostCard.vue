<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ChatDotRound, MoreFilled } from '@element-plus/icons-vue'
import { useMomentsStore, type CommentDto, type PostDto } from '@/stores/moments'
import { useFamilyStore } from '@/stores/family'
import { useSessionStore } from '@/stores/session'
import { openPreview, type PreviewItem } from '@/features/drive/preview'
import MediaTile from './MediaTile.vue'
import { gridColumns, groupReactions, MAX_COMMENT_TEXT, REACTIONS, relativeTime } from './format'

const props = defineProps<{ post: PostDto }>()
const emit = defineEmits<{ deleted: [] }>()
const { t } = useI18n()
const moments = useMomentsStore()
const family = useFamilyStore()
const session = useSessionStore()

const me = computed(() => session.user?.id)
const canDelete = computed(() => props.post.authorId === me.value || session.isAdmin)
const mine = computed(() => new Set(props.post.reactions.filter((r) => r.userId === me.value).map((r) => r.emoji)))
const groups = computed(() => groupReactions(props.post.reactions))
const cols = computed(() => gridColumns(props.post.media.length))
const single = computed(() => props.post.media.length === 1 ? props.post.media[0]! : null)
/** 单张图按原始比例显示（限制最高），宽高未知时按 4:3 */
const singleRatio = computed(() => {
  const m = single.value
  if (!m?.width || !m.height) return '4 / 3'
  const r = m.width / m.height
  return `${Math.min(Math.max(r, 0.6), 2)}`
})

const busy = ref(false)
const composing = ref(false)
const replyTo = ref<CommentDto | null>(null)
const draft = ref('')
const input = ref<{ focus: () => void } | null>(null)
const pickerOpen = ref(false)

function nameOf(id: string | null | undefined): string {
  return id === me.value ? t('moments.me') : family.nameOf(id)
}

function names(ids: string[]): string {
  return ids.map(nameOf).join('、')
}

async function run(fn: () => Promise<unknown>): Promise<void> {
  if (busy.value) return
  busy.value = true
  try {
    await fn()
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    busy.value = false
  }
}

async function confirm(message: string): Promise<boolean> {
  try {
    await ElMessageBox.confirm(message, { type: 'warning', confirmButtonText: t('moments.delete'), cancelButtonText: t('common.cancel') })
    return true
  } catch {
    return false
  }
}

function react(emoji: string): void {
  pickerOpen.value = false
  void run(() => moments.react(props.post.id, emoji))
}

function reactToComment(c: CommentDto): void {
  void run(() => moments.reactToComment(c.id, '❤️'))
}

function startComment(c: CommentDto | null = null): void {
  replyTo.value = c && c.authorId !== me.value ? c : null
  composing.value = true
  void nextTick(() => input.value?.focus())
}

function send(): void {
  const text = draft.value.trim()
  if (!text) return
  void run(async () => {
    await moments.comment(props.post.id, text, replyTo.value?.id ?? null)
    draft.value = ''
    replyTo.value = null
    composing.value = false
  })
}

function canDeleteComment(c: CommentDto): boolean {
  return c.authorId === me.value || props.post.authorId === me.value || session.isAdmin
}

async function removeComment(c: CommentDto): Promise<void> {
  if (await confirm(t('moments.deleteCommentConfirm'))) await run(() => moments.deleteComment(c.id))
}

async function removePost(): Promise<void> {
  if (!(await confirm(t('moments.deletePostConfirm')))) return
  await run(async () => {
    await moments.remove(props.post.id)
    emit('deleted')
  })
}

function preview(index: number): void {
  const items: PreviewItem[] = props.post.media.map((m) => ({ blobId: m.blobId, name: '', mime: m.mime }))
  openPreview(items[index]!, items)
}

function commentLikes(c: CommentDto): number {
  return c.reactions.length
}

function likedComment(c: CommentDto): boolean {
  return c.reactions.some((r) => r.userId === me.value)
}
</script>

<template>
  <article class="rounded-xl p-4" style="background: var(--pc-surface); border: 1px solid var(--pc-border)" data-testid="post-card" :data-post="post.id">
    <header class="mb-2 flex items-center gap-3">
      <el-avatar :size="40" style="background: var(--pc-primary)">{{ nameOf(post.authorId).slice(0, 1) }}</el-avatar>
      <div class="min-w-0 flex-1">
        <div class="truncate font-semibold" data-testid="post-author">{{ nameOf(post.authorId) }}</div>
        <div class="text-xs" style="color: var(--pc-text-muted)">
          <time :datetime="post.createdAt" :title="new Date(post.createdAt).toLocaleString('zh-CN')">{{ relativeTime(post.createdAt) }}</time>
          <span v-if="post.visibility === 'Private'" class="ml-2" data-testid="post-private">🔒 {{ t('moments.private') }}</span>
        </div>
      </div>
      <el-dropdown v-if="canDelete" trigger="click" :persistent="false">
        <el-button text circle :icon="MoreFilled" :aria-label="t('moments.more')" data-testid="post-menu" />
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item data-testid="post-delete" @click="removePost">{{ t('moments.deletePost') }}</el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </header>

    <p v-if="post.text" class="mb-2 whitespace-pre-wrap break-words" data-testid="post-text">{{ post.text }}</p>

    <div v-if="single" class="mb-2 max-w-sm overflow-hidden rounded-lg" :style="{ aspectRatio: singleRatio, maxHeight: '360px' }">
      <MediaTile :blob-id="single.blobId" :mime="single.mime" :duration-seconds="single.durationSeconds" size="thumb-1280" @open="preview(0)" />
    </div>
    <ul
      v-else-if="post.media.length"
      class="mb-2 grid max-w-sm gap-1"
      :style="{ gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))` }"
      data-testid="post-media"
    >
      <li v-for="(m, i) in post.media" :key="m.blobId" class="aspect-square overflow-hidden rounded">
        <MediaTile :blob-id="m.blobId" :mime="m.mime" :duration-seconds="m.durationSeconds" @open="preview(i)" />
      </li>
    </ul>

    <div class="flex items-center gap-1">
      <el-button
        text
        size="small"
        :disabled="busy"
        :aria-pressed="mine.has('❤️')"
        data-testid="post-like"
        @click="react('❤️')"
      >{{ mine.has('❤️') ? '❤️' : '🤍' }} {{ t('moments.like') }}</el-button>
      <el-popover v-model:visible="pickerOpen" trigger="click" placement="top" :width="'auto'" :persistent="false">
        <template #reference>
          <el-button text size="small" :aria-label="t('moments.moreReactions')" data-testid="post-react-more">😊</el-button>
        </template>
        <div class="flex gap-1">
          <button
            v-for="e in REACTIONS"
            :key="e"
            type="button"
            class="rounded px-1 text-xl"
            :style="mine.has(e) ? 'background: var(--pc-primary-soft)' : ''"
            :data-testid="`react-${e}`"
            @click="react(e)"
          >{{ e }}</button>
        </div>
      </el-popover>
      <el-button text size="small" :icon="ChatDotRound" data-testid="post-comment" @click="startComment()">{{ t('moments.comment') }}</el-button>
    </div>

    <div
      v-if="groups.length || post.comments.length || composing"
      class="mt-2 rounded-lg px-3 py-2 text-sm"
      style="background: var(--pc-bg)"
    >
      <p v-for="g in groups" :key="g.emoji" class="mb-1" data-testid="post-reactions">
        <span>{{ g.emoji }}</span> <span style="color: var(--pc-primary)">{{ names(g.userIds) }}</span>
      </p>
      <ul v-if="post.comments.length" :class="groups.length ? 'mt-1 pt-1' : ''" :style="groups.length ? 'border-top: 1px solid var(--pc-border)' : ''">
        <li v-for="c in post.comments" :key="c.id" class="group flex items-start gap-1 py-0.5" data-testid="comment" :data-comment="c.id">
          <button type="button" class="min-w-0 flex-1 text-left break-words" @click="startComment(c)">
            <span class="font-semibold" style="color: var(--pc-primary)">{{ nameOf(c.authorId) }}</span>
            <template v-if="c.replyToUserId">
              <span style="color: var(--pc-text-muted)"> {{ t('moments.replyTo') }} </span>
              <span class="font-semibold" style="color: var(--pc-primary)">{{ nameOf(c.replyToUserId) }}</span>
            </template>
            <span>：{{ c.text }}</span>
          </button>
          <button
            type="button"
            class="shrink-0 text-xs"
            :aria-label="t('moments.likeComment')"
            :aria-pressed="likedComment(c)"
            data-testid="comment-like"
            @click="reactToComment(c)"
          >{{ likedComment(c) ? '❤️' : '♡' }}<span v-if="commentLikes(c)"> {{ commentLikes(c) }}</span></button>
          <button
            v-if="canDeleteComment(c)"
            type="button"
            class="shrink-0 text-xs"
            style="color: var(--pc-text-muted)"
            :aria-label="t('moments.deleteComment')"
            data-testid="comment-delete"
            @click="removeComment(c)"
          >✕</button>
        </li>
      </ul>
      <form v-if="composing" class="mt-2 flex items-center gap-2" @submit.prevent="send">
        <el-input
          ref="input"
          v-model="draft"
          size="small"
          :maxlength="MAX_COMMENT_TEXT"
          :placeholder="replyTo ? t('moments.replyPlaceholder', { name: nameOf(replyTo.authorId) }) : t('moments.commentPlaceholder')"
          data-testid="comment-input"
          @keydown.esc="composing = false"
        />
        <el-button size="small" type="primary" native-type="submit" :loading="busy" :disabled="!draft.trim()" data-testid="comment-send">{{ t('moments.send') }}</el-button>
      </form>
    </div>
  </article>
</template>
