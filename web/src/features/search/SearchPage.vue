<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { Search } from '@element-plus/icons-vue'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { useConversationsStore } from '@/stores/conversations'
import { useFamilyStore } from '@/stores/family'
import { useSessionStore } from '@/stores/session'
import { online } from '@/net/online'
import { blobUrl } from '@/upload'
import { openPreview } from '@/features/drive/preview'
import PreviewHost from '@/features/drive/PreviewHost.vue'
import { formatBytes, hasThumb, KIND_EMOJI, kindOfMime } from '@/features/drive/fileKind'
import { relativeTime } from '@/features/moments/format'
import HighlightText from './HighlightText.vue'
import { searchLocalMessages, type MessageHit } from './local'
import { matchesAll, termsOf } from './text'

type SearchType = components['schemas']['SearchType']
type SearchResult = components['schemas']['SearchResultDto']
type FileHit = components['schemas']['FileHitDto']
type MomentHit = components['schemas']['MomentHitDto']

/**
 * 搜索：聊天消息、网盘文件、时光（动态、评论、相册），外加家人（本地名单里按名字找）。
 * 地址里的 q / type 就是状态：刷新、返回都还在。离线时只在这台设备保存过的聊天记录里找。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const conversations = useConversationsStore()
const family = useFamilyStore()
const session = useSessionStore()

const TYPES: SearchType[] = ['All', 'Messages', 'Files', 'Moments']
const query = computed(() => (typeof route.query.q === 'string' ? route.query.q : ''))
const type = computed<SearchType>(() => (TYPES.includes(route.query.type as SearchType) ? (route.query.type as SearchType) : 'All'))
const input = ref(query.value)
const result = ref<SearchResult | null>(null)
const state = ref<'idle' | 'loading' | 'ok' | 'error'>('idle')
const error = ref('')
const offlineOnly = ref(false)
const loadingMore = ref(false)
/** 点"重试"：同样的查询再跑一次 */
const reload = ref(0)
let gen = 0

const terms = computed(() => termsOf(query.value))
const people = computed(() => {
  if (type.value !== 'All' || !terms.value.length) return []
  return family.members.filter((m) => !m.disabled && m.id !== session.user?.id && terms.value.some((w) => matchesAll(m.displayName, [w]) || matchesAll(m.username, [w])))
})
const empty = computed(() => {
  const r = result.value
  return state.value === 'ok' && !!r && !r.messages.length && !r.files.length && !r.moments.length && !r.albums.length && !people.value.length
})

// 打字时 400 ms 后自动搜；回车立刻搜
let typingTimer: number | undefined
watch(input, (v) => {
  window.clearTimeout(typingTimer)
  typingTimer = window.setTimeout(() => go(v, type.value), 400)
})
watch(query, (q) => {
  if (q !== input.value.trim()) input.value = q
})

onBeforeUnmount(() => window.clearTimeout(typingTimer))

function go(q: string, ty: SearchType): void {
  window.clearTimeout(typingTimer)
  if (route.path !== '/search') return // 已经点进某个结果离开了：迟到的防抖不能把人拉回来
  const trimmed = q.trim()
  if (trimmed === query.value && ty === type.value) return
  void router.replace({ path: '/search', query: { ...(trimmed ? { q: trimmed } : {}), ...(ty !== 'All' ? { type: ty } : {}) } })
}

async function fetchPage(q: string, ty: SearchType, cursor?: string | null): Promise<SearchResult> {
  const r = await api.GET('/api/search', { params: { query: { q, type: ty, cursor: cursor ?? undefined, limit: ty === 'All' ? undefined : 20 } } })
  if (!r.response.ok || !r.data) {
    const p = r.error as { detail?: string; title?: string } | undefined
    throw new Error(p?.detail || p?.title || `HTTP ${r.response.status}`)
  }
  return r.data
}

watch([query, type, online, reload], async ([q, ty, isOnline]) => {
  const my = ++gen
  window.clearTimeout(typingTimer)
  if (!termsOf(q).length) {
    result.value = null
    state.value = 'idle'
    return
  }
  void family.load()
  state.value = 'loading'
  try {
    if (!isOnline) {
      const messages = ty === 'All' || ty === 'Messages' ? await searchLocalMessages(termsOf(q)) : []
      if (my !== gen) return
      offlineOnly.value = true
      result.value = { query: q, terms: termsOf(q), messages, messagesNext: null, files: [], filesNext: null, moments: [], momentsNext: null, albums: [], albumsMore: false }
    } else {
      const r = await fetchPage(q, ty)
      if (my !== gen) return
      offlineOnly.value = false
      result.value = r
    }
    state.value = 'ok'
  } catch (e) {
    if (my !== gen) return
    error.value = e instanceof TypeError ? t('search.unreachable') : (e as Error).message
    state.value = 'error'
  }
}, { immediate: true })

/** 某一类的下一页（只在按类型看时出现） */
async function loadMore(): Promise<void> {
  const r = result.value
  if (!r || loadingMore.value) return
  const cursor = type.value === 'Messages' ? r.messagesNext : type.value === 'Files' ? r.filesNext : r.momentsNext
  if (!cursor) return
  const my = gen
  loadingMore.value = true
  try {
    const next = await fetchPage(query.value, type.value, cursor)
    if (my !== gen || result.value !== r) return
    result.value = {
      ...r,
      messages: [...r.messages, ...next.messages], messagesNext: next.messagesNext,
      files: [...r.files, ...next.files], filesNext: next.filesNext,
      moments: [...r.moments, ...next.moments], momentsNext: next.momentsNext,
    }
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    loadingMore.value = false
  }
}

const hasMore = computed(() => {
  const r = result.value
  if (!r) return false
  return type.value === 'Messages' ? !!r.messagesNext : type.value === 'Files' ? !!r.filesNext : type.value === 'Moments' ? !!r.momentsNext : false
})

// ---- 打开结果 ----

function conversationTitle(id: string): string {
  return family.titleOf(conversations.byId(id)) || t('search.chat')
}

function openMessage(h: MessageHit): void {
  void router.push({ path: `/messages/${h.conversationId}`, query: { seq: String(h.seq) } })
}

const spaceLabel = (f: FileHit) => t(`search.space.${f.space}`)

function folderRoute(f: FileHit, folderId: string | null): string {
  if (f.space === 'Personal') return `/drive/my${folderId ? '/' + folderId : ''}`
  if (f.space === 'Family') return `/drive/family${folderId ? '/' + folderId : ''}`
  return folderId ? `/drive/shared/${f.spaceId}/${folderId}` : '/drive/shared'
}

function openFile(f: FileHit): void {
  if (f.isFolder) {
    void router.push(folderRoute(f, f.nodeId))
    return
  }
  if (f.blobId) openPreview({ blobId: f.blobId, name: f.name, mime: f.mime ?? 'application/octet-stream' })
}

function openFolderOf(f: FileHit): void {
  void router.push(folderRoute(f, f.parentId))
}

function openMoment(m: MomentHit): void {
  void router.push({ path: `/moments/posts/${m.postId}`, query: m.kind === 'comment' ? { comment: m.id } : {} })
}

async function chatWith(userId: string): Promise<void> {
  try {
    const { data } = await api.POST('/api/conversations', { body: { type: 'Direct', title: null, memberIds: [userId] } })
    if (!data) throw new Error()
    if (!conversations.byId(data.id)) await conversations.load()
    await router.push(`/messages/${data.id}`)
  } catch {
    ElMessage.error(t(online.value ? 'search.chatFailed' : 'chat.offlineAction'))
  }
}

function thumb(blobId: string | null | undefined, mime: string | null | undefined): string | null {
  return blobId && hasThumb(kindOfMime(mime)) ? blobUrl(blobId, { v: 'thumb-320' }) : null
}

/** 切页签用输入框里现在的字（可能刚改了还没等到防抖） */
function setType(ty: SearchType): void {
  go(input.value, ty)
}

/** 中文输入法按回车是上屏，不是搜索 */
function onEnter(e: Event): void {
  const k = e as KeyboardEvent
  if (k.isComposing || k.keyCode === 229) return
  go(input.value, type.value)
}

function retry(): void {
  reload.value++
}
</script>

<template>
  <div class="h-full overflow-y-auto" data-testid="search-page">
    <div class="mx-auto flex max-w-2xl flex-col gap-4 p-4">
      <el-input
        v-model="input"
        size="large"
        clearable
        autofocus
        :prefix-icon="Search"
        :placeholder="t('search.placeholder')"
        maxlength="100"
        data-testid="search-input"
        @keydown.enter="onEnter"
      />
      <el-radio-group :model-value="type" @update:model-value="(v) => setType(v as SearchType)">
        <el-radio-button v-for="ty in TYPES" :key="ty" :value="ty" :data-testid="`search-tab-${ty}`">{{ t(`search.tabs.${ty}`) }}</el-radio-button>
      </el-radio-group>

      <el-alert v-if="offlineOnly && state === 'ok'" type="info" :closable="false" show-icon :title="t('search.offline')" data-testid="search-offline" />

      <div v-if="state === 'idle'" class="py-10 text-center text-sm" style="color: var(--pc-text-muted)">{{ t('search.hint') }}</div>
      <el-skeleton v-else-if="state === 'loading' && !result" animated :rows="5" />
      <p v-else-if="state === 'error'" class="text-sm" style="color: #e5484d" data-testid="search-error">
        {{ error }} · <button class="underline" type="button" data-testid="search-retry" @click="retry">{{ t('common.retry') }}</button>
      </p>
      <el-empty v-else-if="empty" :description="t('search.empty', { q: query })" :image-size="72" data-testid="search-empty" />

      <template v-if="result && state !== 'error'">
        <!-- 家人 -->
        <section v-if="people.length">
          <h2 class="mb-2 text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('search.people') }}</h2>
          <ul class="overflow-hidden rounded-2xl border" style="border-color: var(--pc-border); background: var(--pc-surface)">
            <li v-for="m in people" :key="m.id" class="flex items-center gap-3 px-4 py-2.5" data-testid="search-person">
              <el-avatar :size="36" style="background: var(--pc-primary)">{{ (m.displayName || m.username).slice(0, 1) }}</el-avatar>
              <div class="min-w-0 flex-1 truncate"><HighlightText :text="m.displayName || m.username" :terms="terms" /></div>
              <el-button round size="small" @click="chatWith(m.id)">{{ t('search.sendMessage') }}</el-button>
            </li>
          </ul>
        </section>

        <!-- 聊天 -->
        <section v-if="result.messages.length">
          <div class="mb-2 flex items-center justify-between">
            <h2 class="text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('search.tabs.Messages') }}</h2>
            <el-button v-if="type === 'All' && result.messagesNext" text size="small" data-testid="search-more-messages" @click="setType('Messages')">{{ t('search.seeAll') }}</el-button>
          </div>
          <ul class="overflow-hidden rounded-2xl border" style="border-color: var(--pc-border); background: var(--pc-surface)">
            <li v-for="h in result.messages" :key="h.messageId">
              <button type="button" class="flex w-full items-start gap-3 px-4 py-2.5 text-left" data-testid="search-message" :data-message="h.messageId" @click="openMessage(h)">
                <div class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full text-sm text-white" style="background: var(--pc-primary)">
                  {{ h.kind === 'file' ? KIND_EMOJI[kindOfMime(h.mime)] : family.nameOf(h.senderId).slice(0, 1) }}
                </div>
                <div class="min-w-0 flex-1">
                  <div class="flex items-baseline gap-2">
                    <span class="min-w-0 flex-1 truncate text-sm font-medium">{{ conversationTitle(h.conversationId) }}</span>
                    <span class="shrink-0 text-xs" style="color: var(--pc-text-muted)">{{ relativeTime(h.createdAt) }}</span>
                  </div>
                  <div class="line-clamp-2 text-sm" style="color: var(--pc-text-muted)">
                    {{ family.nameOf(h.senderId) }}：<HighlightText :text="h.snippet" :terms="terms" />
                  </div>
                </div>
              </button>
            </li>
          </ul>
        </section>

        <!-- 网盘 -->
        <section v-if="result.files.length">
          <div class="mb-2 flex items-center justify-between">
            <h2 class="text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('search.tabs.Files') }}</h2>
            <el-button v-if="type === 'All' && result.filesNext" text size="small" data-testid="search-more-files" @click="setType('Files')">{{ t('search.seeAll') }}</el-button>
          </div>
          <ul class="overflow-hidden rounded-2xl border" style="border-color: var(--pc-border); background: var(--pc-surface)">
            <li v-for="f in result.files" :key="f.nodeId" class="flex items-center gap-3 px-4 py-2.5" data-testid="search-file" :data-node="f.nodeId">
              <button type="button" class="flex min-w-0 flex-1 items-center gap-3 text-left" @click="openFile(f)">
                <img v-if="thumb(f.blobId, f.mime)" :src="thumb(f.blobId, f.mime)!" alt="" class="h-10 w-10 shrink-0 rounded-lg object-cover" loading="lazy" />
                <span v-else class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg text-xl" style="background: var(--pc-bg)">{{ KIND_EMOJI[kindOfMime(f.mime, f.isFolder)] }}</span>
                <span class="min-w-0 flex-1">
                  <span class="block truncate text-sm font-medium"><HighlightText :text="f.name" :terms="terms" /></span>
                  <span class="block truncate text-xs" style="color: var(--pc-text-muted)">
                    {{ spaceLabel(f) }}<template v-if="!f.isFolder"> · {{ formatBytes(f.size) }}</template> · {{ relativeTime(f.updatedAt) }}
                  </span>
                </span>
              </button>
              <el-button v-if="!f.isFolder && f.parentReadable" text size="small" data-testid="search-file-folder" @click="openFolderOf(f)">{{ t('search.openFolder') }}</el-button>
            </li>
          </ul>
        </section>

        <!-- 时光 -->
        <section v-if="result.albums.length || result.moments.length">
          <div class="mb-2 flex items-center justify-between">
            <h2 class="text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('search.tabs.Moments') }}</h2>
            <el-button v-if="type === 'All' && (result.momentsNext || result.albumsMore)" text size="small" data-testid="search-more-moments" @click="setType('Moments')">{{ t('search.seeAll') }}</el-button>
          </div>
          <div v-if="result.albums.length" class="mb-3 flex gap-3 overflow-x-auto pb-1">
            <router-link v-for="a in result.albums" :key="a.albumId" :to="`/moments/albums/${a.albumId}`" class="w-28 shrink-0" data-testid="search-album">
              <img v-if="a.coverBlobId" :src="blobUrl(a.coverBlobId, { v: 'thumb-320' })" alt="" class="h-28 w-28 rounded-xl object-cover" loading="lazy" />
              <div v-else class="flex h-28 w-28 items-center justify-center rounded-xl text-3xl" style="background: var(--pc-bg)">🖼️</div>
              <div class="mt-1 truncate text-sm"><HighlightText :text="a.title" :terms="terms" /></div>
              <div class="text-xs" style="color: var(--pc-text-muted)">{{ t('search.albumCount', { n: a.itemCount }) }}</div>
            </router-link>
          </div>
          <ul v-if="result.moments.length" class="overflow-hidden rounded-2xl border" style="border-color: var(--pc-border); background: var(--pc-surface)">
            <li v-for="m in result.moments" :key="m.id">
              <button type="button" class="block w-full px-4 py-2.5 text-left" data-testid="search-moment" :data-kind="m.kind" :data-id="m.id" @click="openMoment(m)">
                <div class="flex items-baseline gap-2 text-sm">
                  <span class="min-w-0 flex-1 truncate font-medium">{{ family.nameOf(m.authorId) }}{{ m.kind === 'comment' ? t('search.commented') : t('search.posted') }}</span>
                  <span class="shrink-0 text-xs" style="color: var(--pc-text-muted)">{{ relativeTime(m.createdAt) }}</span>
                </div>
                <div class="line-clamp-2 text-sm" style="color: var(--pc-text-muted)"><HighlightText :text="m.snippet" :terms="terms" /></div>
              </button>
            </li>
          </ul>
        </section>

        <div v-if="hasMore" class="text-center">
          <el-button :loading="loadingMore" data-testid="search-load-more" @click="loadMore">{{ t('moments.loadMore') }}</el-button>
        </div>
      </template>
    </div>
    <PreviewHost />
  </div>
</template>
