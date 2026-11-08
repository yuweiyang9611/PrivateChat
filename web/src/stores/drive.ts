import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { db } from '@/db'
import { onOnline } from '@/net/online'
import { newUuid } from '@/utils/uuid'

export type SpaceDto = components['schemas']['SpaceDto']
export type NodeDto = components['schemas']['NodeDto']
export type ListingDto = components['schemas']['ListingDto']
export type DriveOverviewDto = components['schemas']['DriveOverviewDto']
export type FolderRefDto = components['schemas']['FolderRefDto']
export type TrashItemDto = components['schemas']['TrashItemDto']
export type SharedNodeDto = components['schemas']['SharedNodeDto']
export type ShareDto = components['schemas']['ShareDto']
export type GalleryItemDto = components['schemas']['GalleryItemDto']
export type GalleryPageDto = components['schemas']['GalleryPageDto']
export type DrivePermission = components['schemas']['DrivePermission']
export type SharePermission = components['schemas']['SharePermission']

const RANK: Record<DrivePermission, number> = { None: 0, Read: 1, Write: 2, Owner: 3 }
export function atLeast(p: DrivePermission | undefined, min: DrivePermission): boolean {
  return RANK[p ?? 'None'] >= RANK[min]
}

/** 服务端的 ProblemDetails → 一句给用户看的话 */
export class DriveError extends Error {
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
  throw new DriveError(p?.detail || p?.title || `HTTP ${r.response.status}`, r.response.status)
}

/**
 * 服务器明确的答复（没权限、已删除、参数不对）才算数，离线副本要跟着作废；
 * 5xx / 530（家里服务器关机、隧道断了）和网络错误只说明暂时连不上，保留离线副本。
 */
export function isDefinitive(e: unknown): e is DriveError {
  return e instanceof DriveError && e.status >= 400 && e.status < 500 && e.status !== 408 && e.status !== 429
}

/** DriveChanged 合并：第一次 300 ms 后刷新，之后持续有通知时最多每 1.5 s 刷新一次（批量上传时不会每个文件刷一遍） */
export const CHANGE_DELAY_MS = 300
export const CHANGE_MIN_GAP_MS = 1500

export function listingKey(spaceId: string, folderId: string | null | undefined): string {
  return `${spaceId}:${folderId ?? 'root'}`
}

export const useDriveStore = defineStore('drive', () => {
  const overview = ref<DriveOverviewDto | null>(null)
  const listing = ref<ListingDto | null>(null)
  const currentKey = ref('')
  const loading = ref(false)
  /** 当前显示的是离线副本（连不上服务器） */
  const offlineCopy = ref(false)
  const error = ref<string | null>(null)
  /** 概览拉不到、也没有离线副本（首次离线打开网盘） */
  const overviewFailed = ref(false)
  /** 正在看的目录：出错 / 离线时 listing 为空，刷新、重连仍要知道该刷哪个 */
  let current: { spaceId: string; folderId: string | null } | null = null
  let overviewRequested = false

  const personal = computed(() => overview.value?.spaces.find((s) => s.type === 'Personal'))
  const family = computed(() => overview.value?.spaces.find((s) => s.type === 'Family'))

  async function loadOverview(): Promise<void> {
    overviewRequested = true
    try {
      overview.value = unwrap(await api.GET('/api/drive'))
      overviewFailed.value = false
      await db.driveListings.put({ key: 'overview', listing: overview.value, savedAt: Date.now() }).catch(() => undefined)
    } catch {
      const cached = await db.driveListings.get('overview').catch(() => undefined)
      if (cached && !overview.value) overview.value = cached.listing as DriveOverviewDto
      overviewFailed.value = !overview.value
    }
  }

  /**
   * 打开一个目录：先显示离线副本（如果有），再从服务器刷新。
   * background：推送 / 重连触发的刷新，不转刷新按钮的圈（否则整个文件列表跟着重渲染）。
   */
  async function open(spaceId: string, folderId: string | null, opts: { background?: boolean } = {}): Promise<void> {
    const key = listingKey(spaceId, folderId)
    const switching = currentKey.value !== key
    currentKey.value = key
    current = { spaceId, folderId }
    error.value = null
    if (switching) {
      listing.value = null
      const cached = await db.driveListings.get(key).catch(() => undefined)
      if (cached && currentKey.value === key) listing.value = cached.listing as ListingDto
    }
    const showSpinner = !opts.background
    if (showSpinner) loading.value = true
    try {
      const r = await api.GET('/api/drive/list', { params: { query: { spaceId, folderId: folderId ?? undefined } } })
      const data = unwrap(r)
      if (currentKey.value !== key) return
      listing.value = data
      offlineCopy.value = false
      await db.driveListings.put({ key, listing: data, savedAt: Date.now() }).catch(() => undefined)
    } catch (e) {
      if (currentKey.value !== key) return
      if (isDefinitive(e)) {
        // 服务器明确拒绝（没权限 / 已删除）：离线副本也不能再给看
        listing.value = null
        offlineCopy.value = false
        error.value = e.message
        await db.driveListings.delete(key).catch(() => undefined)
      } else {
        offlineCopy.value = listing.value !== null
        if (!listing.value) error.value = 'offline'
      }
    } finally {
      if (showSpinner && currentKey.value === key) loading.value = false
    }
  }

  async function refresh(opts: { background?: boolean } = {}): Promise<void> {
    if (current) await open(current.spaceId, current.folderId, opts)
  }

  /** 恢复联网 / 重新连上服务器：用过网盘的话把概览和正在看的目录重新拉一遍（出错、离线状态靠这个恢复） */
  function resync(): void {
    if (!overviewRequested && !current) return
    void loadOverview()
    void refresh({ background: true })
  }

  /** DriveChanged：正看着的目录变了就刷新；用量也顺手刷新 */
  let changeTimer: ReturnType<typeof setTimeout> | undefined
  let lastFlush = 0
  const pendingKeys = new Set<string>()
  function onChanged(e: { spaceId: string; parentId?: string | null }): void {
    pendingKeys.add(listingKey(e.spaceId, e.parentId))
    if (changeTimer) return
    changeTimer = setTimeout(flushChanges, Math.max(CHANGE_DELAY_MS, lastFlush + CHANGE_MIN_GAP_MS - Date.now()))
  }
  function flushChanges(): void {
    changeTimer = undefined
    lastFlush = Date.now()
    const keys = new Set(pendingKeys)
    pendingKeys.clear()
    if (keys.has(currentKey.value)) void refresh({ background: true })
    void loadOverview()
    for (const k of keys) if (k !== currentKey.value) void db.driveListings.delete(k).catch(() => undefined)
  }

  if (typeof window !== 'undefined') onOnline(resync)

  // ---------------- 操作 ----------------

  async function createFolder(spaceId: string, parentId: string | null, name: string): Promise<NodeDto> {
    const node = unwrap(await api.POST('/api/drive/folders', { body: { spaceId, parentId, name } }))
    await refreshIfShowing(spaceId, parentId)
    return node
  }

  /** refresh: false 给上传队列用——不等整页刷新，改走 onChanged 的合并刷新 */
  async function addFile(spaceId: string, parentId: string | null, blobId: string, name: string, opts: { refresh?: boolean } = {}): Promise<NodeDto> {
    const node = unwrap(await api.POST('/api/drive/files', { body: { spaceId, parentId, blobId, name, onConflict: 'Rename' } }))
    if (opts.refresh !== false) await refreshIfShowing(spaceId, parentId)
    return node
  }

  async function rename(node: NodeDto, name: string): Promise<void> {
    unwrap(await api.PATCH('/api/drive/nodes/{id}', { params: { path: { id: node.id } }, body: { name, moveTo: null } }))
    await refreshIfShowing(node.spaceId, node.parentId ?? null)
  }

  async function move(node: NodeDto, target: { spaceId: string; parentId: string | null }): Promise<void> {
    unwrap(await api.PATCH('/api/drive/nodes/{id}', { params: { path: { id: node.id } }, body: { name: null, moveTo: target } }))
    await refreshIfShowing(node.spaceId, node.parentId ?? null)
  }

  async function remove(node: NodeDto): Promise<void> {
    const r = await api.DELETE('/api/drive/nodes/{id}', { params: { path: { id: node.id } } })
    if (!r.response.ok) unwrap(r)
    await refreshIfShowing(node.spaceId, node.parentId ?? null)
    void loadOverview()
  }

  async function folders(spaceId: string): Promise<FolderRefDto[]> {
    return unwrap(await api.GET('/api/drive/spaces/{id}/folders', { params: { path: { id: spaceId } } }))
  }

  async function trash(): Promise<TrashItemDto[]> {
    return unwrap(await api.GET('/api/drive/trash'))
  }

  async function restore(id: string): Promise<NodeDto> {
    return unwrap(await api.POST('/api/drive/trash/{id}/restore', { params: { path: { id } } }))
  }

  async function purge(id: string): Promise<void> {
    const r = await api.DELETE('/api/drive/trash/{id}', { params: { path: { id } } })
    if (!r.response.ok) unwrap(r)
    void loadOverview()
  }

  async function emptyTrash(): Promise<void> {
    unwrap(await api.DELETE('/api/drive/trash'))
    void loadOverview()
  }

  async function sharedWithMe(): Promise<SharedNodeDto[]> {
    return unwrap(await api.GET('/api/drive/shared-with-me'))
  }

  async function shares(nodeId: string): Promise<ShareDto[]> {
    return unwrap(await api.GET('/api/drive/nodes/{id}/shares', { params: { path: { id: nodeId } } }))
  }

  async function share(nodeId: string, body: { granteeUserId?: string | null; permission?: SharePermission; link?: boolean; password?: string | null; expiresInDays?: number | null }): Promise<ShareDto> {
    return unwrap(await api.POST('/api/drive/nodes/{id}/shares', {
      params: { path: { id: nodeId } },
      body: { granteeUserId: body.granteeUserId ?? null, permission: body.permission ?? 'Read', link: body.link ?? false, password: body.password ?? null, expiresInDays: body.expiresInDays ?? null },
    }))
  }

  async function revoke(shareId: string): Promise<void> {
    const r = await api.DELETE('/api/drive/shares/{id}', { params: { path: { id: shareId } } })
    if (!r.response.ok) unwrap(r)
  }

  async function sendToChat(nodeId: string, conversationId: string): Promise<void> {
    unwrap(await api.POST('/api/drive/nodes/{id}/send', { params: { path: { id: nodeId } }, body: { conversationId, clientMessageId: newUuid() } }))
  }

  async function gallery(before?: string | null, beforeId?: string | null, limit = 60): Promise<GalleryPageDto> {
    return unwrap(await api.GET('/api/drive/gallery', { params: { query: { before: before ?? undefined, beforeId: beforeId ?? undefined, limit } } }))
  }

  /** 聊天里的文件 / 图片存到"我的文件"根目录 */
  async function saveToMyFiles(blobId: string, name: string): Promise<NodeDto> {
    if (!personal.value) await loadOverview()
    const space = personal.value
    if (!space) throw new DriveError('offline', 0)
    return await addFile(space.id, null, blobId, name)
  }

  async function refreshIfShowing(spaceId: string, parentId: string | null): Promise<void> {
    if (currentKey.value === listingKey(spaceId, parentId)) await refresh({ background: true })
  }

  return {
    overview, overviewFailed, listing, currentKey, loading, offlineCopy, error, personal, family,
    loadOverview, open, refresh, resync, onChanged,
    createFolder, addFile, rename, move, remove, folders, trash, restore, purge, emptyTrash,
    sharedWithMe, shares, share, revoke, sendToChat, gallery, saveToMyFiles,
  }
})
