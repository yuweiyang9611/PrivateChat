import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '@/api/client'
import type { components } from '@/api/schema'

export type PostDto = components['schemas']['PostDto']
export type CommentDto = components['schemas']['CommentDto']
export type PostVisibility = components['schemas']['PostVisibility']
export type OnThisDayDto = components['schemas']['OnThisDayDto']
export type AlbumDto = components['schemas']['AlbumDto']
export type AlbumsOverviewDto = components['schemas']['AlbumsOverviewDto']
export type AlbumItemDto = components['schemas']['AlbumItemDto']
export type AlbumItemsPageDto = components['schemas']['AlbumItemsPageDto']
export type GalleryMonthDto = components['schemas']['GalleryMonthDto']
export type GalleryPageDto = components['schemas']['GalleryPageDto']

export interface PostChangedEvent { postId: string; authorId: string; change: 'created' | 'updated' | 'deleted' }
export interface AlbumChangedEvent { albumId: string; deleted: boolean }

export class MomentsError extends Error {
  readonly status: number
  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

type ApiResult<T> = { data?: T; error?: unknown; response: Response }
function unwrap<T>(r: ApiResult<T>): T {
  if (r.response.ok) return r.data as T
  const p = r.error as { detail?: string; title?: string } | undefined
  throw new MomentsError(p?.detail || p?.title || `HTTP ${r.response.status}`, r.response.status)
}

/** 新的在前：先比时间，再比 id（与服务端游标一致） */
export function newerFirst(a: PostDto, b: PostDto): number {
  const d = Date.parse(b.createdAt) - Date.parse(a.createdAt)
  if (d !== 0) return d
  return a.id < b.id ? 1 : a.id > b.id ? -1 : 0
}

const PAGE = 20

/**
 * 时光：动态时间线、单条动态缓存、那年今天、相册。
 * 每次评论 / 点赞 / 删除后服务端返回整条动态，这里统一 upsert；别人改动经 SignalR PostChanged 到达后重新取那一条。
 */
export const useMomentsStore = defineStore('moments', () => {
  const posts = ref<PostDto[]>([])
  /** 单条动态（时间线里的、单独打开的、那年今天里的），按 id */
  const cache = ref<Record<string, PostDto>>({})
  const next = ref<{ before: string; beforeId: string } | null>(null)
  const loaded = ref(false)
  const loading = ref(false)
  const error = ref<string | null>(null)
  const onThisDay = ref<OnThisDayDto | null>(null)
  const albums = ref<AlbumsOverviewDto | null>(null)
  /** 某个相册被改了：打开着的相册页看到版本号变化就重新加载 */
  const albumVersion = ref<Record<string, number>>({})
  /** 每次重新加载第一页 +1：还在路上的翻页回来时发现变了就丢掉，免得把老的一页接到新列表后面、中间缺一段 */
  let feedGen = 0

  function upsert(p: PostDto, opts: { insert?: boolean } = {}): void {
    cache.value[p.id] = p
    const i = posts.value.findIndex((x) => x.id === p.id)
    if (i >= 0) {
      posts.value[i] = p
    } else if (opts.insert && loaded.value) {
      // 只有落在已加载的范围里才插进时间线；更老的等翻页时自然加载
      const last = posts.value[posts.value.length - 1]
      if (!next.value || !last || newerFirst(p, last) <= 0) {
        posts.value.push(p)
        posts.value.sort(newerFirst)
      }
    }
    if (onThisDay.value) {
      for (const y of onThisDay.value.years) {
        const j = y.posts.findIndex((x) => x.id === p.id)
        if (j >= 0) y.posts[j] = p
      }
    }
  }

  function drop(id: string): void {
    posts.value = posts.value.filter((p) => p.id !== id)
    delete cache.value[id]
    if (onThisDay.value) for (const y of onThisDay.value.years) y.posts = y.posts.filter((p) => p.id !== id)
  }

  async function loadFirst(): Promise<void> {
    const gen = ++feedGen
    loading.value = true
    error.value = null
    try {
      const page = unwrap(await api.GET('/api/posts', { params: { query: { limit: PAGE } } }))
      if (gen !== feedGen) return
      posts.value = page.items
      for (const p of page.items) cache.value[p.id] = p
      next.value = page.nextBefore && page.nextBeforeId ? { before: page.nextBefore, beforeId: page.nextBeforeId } : null
      loaded.value = true
    } catch (e) {
      if (gen === feedGen) error.value = (e as Error).message
    } finally {
      if (gen === feedGen) loading.value = false
    }
  }

  async function loadMore(): Promise<void> {
    if (!next.value || loading.value) return
    const gen = feedGen
    loading.value = true
    error.value = null
    try {
      const page = unwrap(await api.GET('/api/posts', { params: { query: { limit: PAGE, before: next.value.before, beforeId: next.value.beforeId } } }))
      if (gen !== feedGen) return
      const have = new Set(posts.value.map((p) => p.id))
      posts.value.push(...page.items.filter((p) => !have.has(p.id)))
      for (const p of page.items) cache.value[p.id] = p
      next.value = page.nextBefore && page.nextBeforeId ? { before: page.nextBefore, beforeId: page.nextBeforeId } : null
    } catch (e) {
      if (gen === feedGen) error.value = (e as Error).message
    } finally {
      if (gen === feedGen) loading.value = false
    }
  }

  /**
   * 合并最新一页（新的插进来、改过的替换），不动已经往下翻出来的内容。
   * 这一页覆盖的时间范围里、本地有而服务器没返回的，是断线期间被删掉（或改成私密）的：去掉。
   */
  async function refreshTop(): Promise<void> {
    try {
      const page = unwrap(await api.GET('/api/posts', { params: { query: { limit: PAGE } } }))
      const ids = new Set(page.items.map((p) => p.id))
      const last = page.items[page.items.length - 1]
      // 没有下一页 = 服务器上一共就这些；否则只覆盖到这一页最后一条为止
      const covered = (p: PostDto): boolean => !page.nextBefore || !last || newerFirst(p, last) <= 0
      for (const p of posts.value.filter((x) => !ids.has(x.id) && covered(x))) drop(p.id)
      for (const p of page.items) upsert(p, { insert: true })
    } catch {
      // 还连不上：下次重连再补
    }
  }

  async function loadOnThisDay(): Promise<void> {
    try {
      onThisDay.value = unwrap(await api.GET('/api/posts/on-this-day'))
    } catch {
      // 那年今天只是锦上添花：失败就不显示
    }
  }

  async function fetchPost(id: string): Promise<PostDto> {
    const p = unwrap(await api.GET('/api/posts/{id}', { params: { path: { id } } }))
    upsert(p)
    return p
  }

  async function create(text: string | null, visibility: PostVisibility, blobIds: string[]): Promise<PostDto> {
    const p = unwrap(await api.POST('/api/posts', { body: { text, visibility, blobIds } }))
    upsert(p, { insert: true })
    return p
  }

  async function remove(id: string): Promise<void> {
    unwrap(await api.DELETE('/api/posts/{id}', { params: { path: { id } } }))
    drop(id)
  }

  async function comment(postId: string, text: string, replyToCommentId: string | null = null): Promise<PostDto> {
    const p = unwrap(await api.POST('/api/posts/{id}/comments', { params: { path: { id: postId } }, body: { text, replyToCommentId } }))
    upsert(p)
    return p
  }

  async function deleteComment(commentId: string): Promise<PostDto> {
    const p = unwrap(await api.DELETE('/api/comments/{id}', { params: { path: { id: commentId } } }))
    upsert(p)
    return p
  }

  async function react(postId: string, emoji: string): Promise<PostDto> {
    const p = unwrap(await api.POST('/api/posts/{id}/reactions', { params: { path: { id: postId } }, body: { emoji } }))
    upsert(p)
    return p
  }

  async function reactToComment(commentId: string, emoji: string): Promise<PostDto> {
    const p = unwrap(await api.POST('/api/comments/{id}/reactions', { params: { path: { id: commentId } }, body: { emoji } }))
    upsert(p)
    return p
  }

  /** 连续的点赞 / 评论事件合并：同一条动态 300 ms 内只取一次 */
  const pending = new Map<string, { timer: ReturnType<typeof setTimeout>; created: boolean }>()
  function onPostChanged(e: PostChangedEvent): void {
    if (e.change === 'deleted') {
      const p = pending.get(e.postId)
      if (p) clearTimeout(p.timer)
      pending.delete(e.postId)
      drop(e.postId)
      return
    }
    // 没打开过时光页、也不在缓存里：用不着，等打开时再取
    if (!loaded.value && !cache.value[e.postId]) return
    const created = (pending.get(e.postId)?.created ?? false) || e.change === 'created'
    const old = pending.get(e.postId)
    if (old) clearTimeout(old.timer)
    pending.set(e.postId, {
      created,
      timer: setTimeout(() => {
        pending.delete(e.postId)
        void api.GET('/api/posts/{id}', { params: { path: { id: e.postId } } }).then((r) => {
          if (r.response.ok && r.data) upsert(r.data, { insert: created })
          else if (r.response.status === 404) drop(e.postId)
        }).catch(() => undefined)
      }, 300),
    })
  }

  // ---------------- 相册 ----------------

  async function loadAlbums(): Promise<void> {
    albums.value = unwrap(await api.GET('/api/albums'))
  }

  async function createAlbum(title: string, blobIds: string[] = []): Promise<AlbumDto> {
    const a = unwrap(await api.POST('/api/albums', { body: { title, blobIds } }))
    await loadAlbums().catch(() => undefined)
    return a
  }

  async function patchAlbum(id: string, body: { title?: string; coverBlobId?: string; clearCover?: boolean }): Promise<AlbumDto> {
    const a = unwrap(await api.PATCH('/api/albums/{id}', { params: { path: { id } }, body }))
    replaceAlbum(a)
    return a
  }

  async function deleteAlbum(id: string): Promise<void> {
    unwrap(await api.DELETE('/api/albums/{id}', { params: { path: { id } } }))
    if (albums.value) albums.value.albums = albums.value.albums.filter((a) => a.id !== id)
  }

  async function albumItems(id: string, before?: string | null, beforeId?: string | null, limit = 60): Promise<AlbumItemsPageDto> {
    return unwrap(await api.GET('/api/albums/{id}/items', { params: { path: { id }, query: { before: before ?? undefined, beforeId: beforeId ?? undefined, limit } } }))
  }

  async function addToAlbum(id: string, blobIds: string[]): Promise<number> {
    const r = unwrap(await api.POST('/api/albums/{id}/items', { params: { path: { id } }, body: { blobIds } }))
    replaceAlbum(r.album)
    return r.added
  }

  async function removeFromAlbum(id: string, blobId: string): Promise<AlbumDto> {
    const a = unwrap(await api.DELETE('/api/albums/{id}/items/{blobId}', { params: { path: { id, blobId } } }))
    replaceAlbum(a)
    return a
  }

  /** 按月相册的一页：时间线限定在 [since, before) */
  async function monthItems(since: string, before: string, beforeId?: string | null, limit = 60): Promise<GalleryPageDto> {
    return unwrap(await api.GET('/api/drive/gallery', { params: { query: { since, before, beforeId: beforeId ?? undefined, limit } } }))
  }

  function replaceAlbum(a: AlbumDto): void {
    if (!albums.value) return
    const i = albums.value.albums.findIndex((x) => x.id === a.id)
    if (i >= 0) albums.value.albums[i] = a
  }

  let albumsTimer: ReturnType<typeof setTimeout> | null = null
  function onAlbumChanged(e: AlbumChangedEvent): void {
    albumVersion.value[e.albumId] = (albumVersion.value[e.albumId] ?? 0) + 1
    if (!albums.value) return
    if (albumsTimer) clearTimeout(albumsTimer)
    albumsTimer = setTimeout(() => {
      albumsTimer = null
      void loadAlbums().catch(() => undefined)
    }, 300)
  }

  /** 重新连上服务器后：错过的实时事件靠重新拉第一页、相册列表补上 */
  function resync(): void {
    if (loaded.value) void refreshTop()
    if (albums.value) void loadAlbums().catch(() => undefined)
  }

  return {
    posts, cache, next, loaded, loading, error, onThisDay, albums, albumVersion,
    upsert, loadFirst, loadMore, loadOnThisDay, fetchPost, create, remove, comment, deleteComment, react, reactToComment, onPostChanged, resync,
    loadAlbums, createAlbum, patchAlbum, deleteAlbum, albumItems, addToAlbum, removeFromAlbum, monthItems, onAlbumChanged,
  }
})
