import { defineStore } from 'pinia'
import { computed, markRaw, ref } from 'vue'
import { isOnline, onOnline } from '@/net/online'
import { uploadFile, type UploadHandle } from '@/upload'
import { newUuid } from '@/utils/uuid'
import { DriveError, useDriveStore } from './drive'

export type UploadStatus = 'queued' | 'uploading' | 'paused' | 'registering' | 'done' | 'failed'

export interface UploadItem {
  id: string
  file: File
  spaceId: string
  parentId: string | null
  name: string
  status: UploadStatus
  /** 0–1 */
  progress: number
  error?: string
  handle?: UploadHandle
}

/** 同时传两个：手机上行带宽有限，多开反而更慢 */
export const MAX_PARALLEL = 2
/** 进度最多每 250 ms 写一次：tus 每 ~50 ms 报一次，写一次就要重渲染一次上传面板 */
export const PROGRESS_INTERVAL_MS = 250

/**
 * 网盘上传队列：秒传 / tus 分片断点续传 → 登记成网盘文件。
 * 可暂停、继续、取消、失败重试；断网时排队，联网后自动继续（tus 按指纹续传）。
 * 队列只在内存里：刷新页面会丢（离开页面前会提醒）。
 */
export const useDriveUploadsStore = defineStore('driveUploads', () => {
  const items = ref<UploadItem[]>([])

  const active = computed(() => items.value.filter((i) => i.status === 'uploading' || i.status === 'registering').length)
  const unfinished = computed(() => items.value.filter((i) => i.status !== 'done' && i.status !== 'failed').length)
  const totalProgress = computed(() => {
    const live = items.value.filter((i) => i.status !== 'failed')
    const total = live.reduce((n, i) => n + i.file.size, 0)
    return total ? live.reduce((n, i) => n + i.file.size * (i.status === 'done' ? 1 : i.progress), 0) / total : 0
  })

  function find(id: string): UploadItem | undefined {
    return items.value.find((i) => i.id === id)
  }

  function add(files: File[], spaceId: string, parentId: string | null): void {
    for (const file of files) {
      items.value.push({ id: newUuid(), file: markRaw(file), spaceId, parentId, name: file.name, status: 'queued', progress: 0 })
    }
    pump()
  }

  function pump(): void {
    if (!isOnline()) return
    while (active.value < MAX_PARALLEL) {
      const next = items.value.find((i) => i.status === 'queued')
      if (!next) return
      void start(next.id)
    }
  }

  /** 节流写进度（首次立即写，之后最多每 PROGRESS_INTERVAL_MS 一次，最后一个值不会丢） */
  function progressWriter(item: UploadItem): (r: number) => void {
    let last = 0
    let latest = 0
    let timer: ReturnType<typeof setTimeout> | undefined
    const write = () => {
      timer = undefined
      last = Date.now()
      if (item.status === 'uploading' || item.status === 'paused') item.progress = latest
    }
    return (r) => {
      latest = r
      if (timer) return
      const wait = last + PROGRESS_INTERVAL_MS - Date.now()
      if (wait <= 0) write()
      else timer = setTimeout(write, wait)
    }
  }

  async function start(id: string): Promise<void> {
    const item = find(id)
    if (!item) return
    item.status = 'uploading'
    item.error = undefined
    const handle = uploadFile(item.file, { onProgress: progressWriter(item) })
    item.handle = markRaw(handle)
    try {
      const blobId = await handle.done
      const it = find(id)
      if (!it) return // 已取消
      it.status = 'registering'
      const drive = useDriveStore()
      // 登记完就让出名额：不等整页刷新（几千个文件时每个都刷一遍是 N²），目录交给合并刷新
      await drive.addFile(it.spaceId, it.parentId, blobId, it.name, { refresh: false })
      it.status = 'done'
      it.progress = 1
      drive.onChanged({ spaceId: it.spaceId, parentId: it.parentId })
    } catch (e) {
      const it = find(id)
      if (!it) return
      if (!isOnline()) {
        // 断网：回到队列，联网后从断点继续
        it.status = 'queued'
      } else {
        it.status = 'failed'
        it.error = e instanceof DriveError ? e.message : e instanceof Error ? e.message : String(e)
      }
    } finally {
      const it = find(id)
      if (it) it.handle = undefined
      pump()
    }
  }

  function pause(id: string): void {
    const it = find(id)
    if (!it) return
    if (it.status === 'uploading' && it.handle) {
      it.handle.pause()
      it.status = 'paused'
      pump()
    } else if (it.status === 'queued') {
      it.status = 'paused'
    }
  }

  function resume(id: string): void {
    const it = find(id)
    if (!it || it.status !== 'paused') return
    if (it.handle) {
      it.status = 'uploading'
      it.handle.resume()
    } else {
      it.status = 'queued'
      pump()
    }
  }

  function retry(id: string): void {
    const it = find(id)
    if (!it || it.status !== 'failed') return
    it.status = 'queued'
    it.progress = 0
    pump()
  }

  function cancel(id: string): void {
    const it = find(id)
    if (!it) return
    items.value = items.value.filter((i) => i.id !== id)
    void it.handle?.abort()
    pump()
  }

  function clearFinished(): void {
    items.value = items.value.filter((i) => i.status !== 'done')
  }

  if (typeof window !== 'undefined') {
    onOnline(pump)
    // 还有没传完的就提醒一下，避免误关页面
    window.addEventListener('beforeunload', (e) => {
      if (unfinished.value > 0) {
        e.preventDefault()
        e.returnValue = ''
      }
    })
  }

  return { items, active, unfinished, totalProgress, add, pause, resume, retry, cancel, clearFinished }
})
