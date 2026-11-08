import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'

interface FakeUpload { file: File; resolve: (id: string) => void; reject: (e: unknown) => void; paused: boolean; aborted: boolean; progress: (r: number) => void }

const ctl = vi.hoisted(() => ({
  online: true,
  uploads: [] as FakeUpload[],
  added: [] as { spaceId: string; parentId: string | null; blobId: string; name: string; opts?: { refresh?: boolean } }[],
  changed: [] as { spaceId: string; parentId?: string | null }[],
  addFails: null as null | Error,
}))

vi.mock('@/upload', () => ({
  uploadFile: (file: File, opts: { onProgress?: (r: number) => void } = {}) => {
    let resolve!: (id: string) => void
    let reject!: (e: unknown) => void
    const done = new Promise<string>((res, rej) => { resolve = res; reject = rej })
    const u: FakeUpload = { file, resolve, reject, paused: false, aborted: false, progress: (r) => opts.onProgress?.(r) }
    ctl.uploads.push(u)
    return {
      done,
      abort: async () => { u.aborted = true; reject(new Error('aborted')) },
      pause: () => { u.paused = true },
      resume: () => { u.paused = false },
    }
  },
}))

vi.mock('@/net/online', () => ({ isOnline: () => ctl.online, onOnline: () => () => undefined }))

vi.mock('@/stores/drive', () => {
  class DriveError extends Error {}
  return {
    DriveError,
    useDriveStore: () => ({
      addFile: async (spaceId: string, parentId: string | null, blobId: string, name: string, opts?: { refresh?: boolean }) => {
        if (ctl.addFails) throw ctl.addFails
        ctl.added.push({ spaceId, parentId, blobId, name, opts })
      },
      onChanged: (e: { spaceId: string; parentId?: string | null }) => { ctl.changed.push(e) },
    }),
  }
})

const { useDriveUploadsStore, MAX_PARALLEL, PROGRESS_INTERVAL_MS } = await import('./driveUploads')

const file = (name: string, size = 10) => new File(['x'.repeat(size)], name)
const tick = () => new Promise((r) => setTimeout(r, 0))

beforeEach(() => {
  setActivePinia(createPinia())
  Object.assign(ctl, { online: true, uploads: [], added: [], changed: [], addFails: null })
})

afterEach(() => {
  vi.useRealTimers()
})

describe('网盘上传队列', () => {
  it('最多同时传两个，传完一个自动开始下一个，并登记到对应文件夹', async () => {
    const q = useDriveUploadsStore()
    q.add([file('a'), file('b'), file('c')], 'space', 'folder')
    expect(ctl.uploads).toHaveLength(MAX_PARALLEL)
    expect(q.items.map((i) => i.status)).toEqual(['uploading', 'uploading', 'queued'])

    ctl.uploads[0]!.resolve('blob-a')
    await tick(); await tick()
    // 登记时不等整页刷新，改走合并刷新（批量上传不会每个文件刷一遍目录）
    expect(ctl.added).toEqual([{ spaceId: 'space', parentId: 'folder', blobId: 'blob-a', name: 'a', opts: { refresh: false } }])
    expect(ctl.changed).toEqual([{ spaceId: 'space', parentId: 'folder' }])
    expect(q.items[0]!.status).toBe('done')
    expect(ctl.uploads).toHaveLength(3)
    expect(q.items[2]!.status).toBe('uploading')
  })

  it('暂停让出名额，继续时接着传', async () => {
    const q = useDriveUploadsStore()
    q.add([file('a'), file('b'), file('c')], 's', null)
    const a = q.items[0]!.id
    q.pause(a)
    expect(ctl.uploads[0]!.paused).toBe(true)
    expect(q.items[0]!.status).toBe('paused')
    expect(q.items[2]!.status).toBe('uploading') // 让出的名额给了第三个

    q.resume(a)
    expect(ctl.uploads[0]!.paused).toBe(false)
    expect(q.items[0]!.status).toBe('uploading')
  })

  it('断网导致失败时回到队列，联网前不重新开始', async () => {
    const q = useDriveUploadsStore()
    q.add([file('a')], 's', null)
    ctl.online = false
    ctl.uploads[0]!.reject(new TypeError('Failed to fetch'))
    await tick(); await tick()
    expect(q.items[0]!.status).toBe('queued')
    expect(ctl.uploads).toHaveLength(1)
  })

  it('联网时失败显示原因，可重试；取消会中止上传并移出列表', async () => {
    const q = useDriveUploadsStore()
    q.add([file('a'), file('b')], 's', null)
    ctl.addFails = new Error('空间已满')
    ctl.uploads[0]!.resolve('blob-a')
    await tick(); await tick()
    expect(q.items[0]!.status).toBe('failed')
    expect(q.items[0]!.error).toBe('空间已满')

    ctl.addFails = null
    q.retry(q.items[0]!.id)
    expect(q.items[0]!.status).toBe('uploading')

    const b = q.items[1]!.id
    q.cancel(b)
    await tick()
    expect(ctl.uploads[1]!.aborted).toBe(true)
    expect(q.items.find((i) => i.id === b)).toBeUndefined()
  })

  it('总进度按文件大小加权，清除只清已完成的', async () => {
    const q = useDriveUploadsStore()
    q.add([file('big', 300), file('small', 100)], 's', null)
    q.items[0]!.progress = 0.5
    expect(q.totalProgress).toBeCloseTo(150 / 400)
    ctl.uploads[1]!.resolve('blob-s')
    await tick(); await tick()
    q.clearFinished()
    expect(q.items.map((i) => i.name)).toEqual(['big'])
  })

  it('进度节流：第一次立即写，之后最多每 250 ms 写一次，最后的值不丢', async () => {
    vi.useFakeTimers()
    const q = useDriveUploadsStore()
    q.add([file('a')], 's', null)
    const u = ctl.uploads[0]!
    u.progress(0.1)
    expect(q.items[0]!.progress).toBe(0.1)
    u.progress(0.2)
    u.progress(0.3)
    expect(q.items[0]!.progress).toBe(0.1)
    vi.advanceTimersByTime(PROGRESS_INTERVAL_MS)
    expect(q.items[0]!.progress).toBe(0.3)

    // 传完之后迟到的进度不会把"完成"改回去
    u.progress(0.9)
    u.progress(0.95)
    u.resolve('blob-a')
    await vi.advanceTimersByTimeAsync(PROGRESS_INTERVAL_MS)
    expect(q.items[0]!.status).toBe('done')
    expect(q.items[0]!.progress).toBe(1)
  })
})
