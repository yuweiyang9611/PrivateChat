import { createSHA256 } from 'hash-wasm'
import * as tus from 'tus-js-client'
import { api } from '@/api/client'

/** 与后端 Kestrel 64 MB 限制、Cloudflare 100 MB 限制留足余量 */
export const CHUNK_SIZE = 8 * 1024 * 1024
const HASH_CHUNK = 4 * 1024 * 1024

export type AttachmentKind = 'Image' | 'Video' | 'Audio' | 'File'

/** 按 MIME 判断消息类型；SVG 不当图片（可能带脚本） */
export function kindOf(file: File): AttachmentKind {
  const t = file.type
  if (t.startsWith('image/') && t !== 'image/svg+xml') return 'Image'
  if (t.startsWith('video/')) return 'Video'
  if (t.startsWith('audio/')) return 'Audio'
  return 'File'
}

/** 流式算 SHA-256（hash-wasm），大文件不会把整份读进内存 */
export async function sha256Of(file: File, onProgress?: (ratio: number) => void): Promise<string> {
  const hasher = await createSHA256()
  hasher.init()
  for (let offset = 0; offset < file.size; offset += HASH_CHUNK) {
    const buf = await file.slice(offset, offset + HASH_CHUNK).arrayBuffer()
    hasher.update(new Uint8Array(buf))
    onProgress?.(Math.min(1, (offset + buf.byteLength) / file.size))
  }
  return hasher.digest('hex')
}

export interface ImageSize { width: number; height: number }

/** 读图片尺寸，供消息气泡预留占位，避免加载时跳动 */
export function imageSize(file: File): Promise<ImageSize | null> {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file)
    const img = new Image()
    img.onload = () => { resolve({ width: img.naturalWidth, height: img.naturalHeight }); URL.revokeObjectURL(url) }
    img.onerror = () => { resolve(null); URL.revokeObjectURL(url) }
    img.src = url
  })
}

export interface UploadHandle {
  /** 返回 blobId */
  done: Promise<string>
  /** 取消并删除服务器上的半截文件 */
  abort: () => Promise<void>
  /** 暂停（服务器保留已传部分）/ 继续（从断点续传） */
  pause: () => void
  resume: () => void
}

export interface UploadOptions {
  sha256?: string
  /** 0–1 */
  onProgress?: (ratio: number) => void
}

/**
 * 上传一个文件并返回 blobId：
 * 1. 先 precheck 秒传；
 * 2. 否则 tus 分片上传到 /api/uploads，断网自动重试，刷新后可按指纹续传；
 * 3. 最后一个分片的响应头 X-Blob-Id 就是结果。
 */
export function uploadFile(file: File, opts: UploadOptions = {}): UploadHandle {
  let upload: tus.Upload | null = null
  let aborted = false
  let paused = false
  /** tus 对象建好、续传信息也查过了，才允许 start */
  let ready = false
  const startIfAllowed = (): void => {
    if (upload && ready && !aborted && !paused) upload.start()
  }

  const done = (async () => {
    const sha256 = opts.sha256 ?? (await sha256Of(file))
    const pre = await api.POST('/api/blobs/precheck', { body: { sha256, size: file.size } })
    if (pre.data?.exists && pre.data.blobId) {
      opts.onProgress?.(1)
      return pre.data.blobId
    }
    if (aborted) throw new Error('aborted')

    return await new Promise<string>((resolve, reject) => {
      let blobId: string | undefined
      upload = new tus.Upload(file, {
        endpoint: '/api/uploads',
        chunkSize: CHUNK_SIZE,
        retryDelays: [0, 1000, 3000, 5000, 10000],
        removeFingerprintOnSuccess: true,
        metadata: { filename: file.name, filetype: file.type || 'application/octet-stream', sha256 },
        onProgress: (sent, total) => opts.onProgress?.(total ? sent / total : 0),
        onAfterResponse: (_req, res) => {
          const id = res.getHeader('X-Blob-Id')
          if (id) blobId = id
        },
        onSuccess: (payload) => {
          const id = blobId ?? payload.lastResponse?.getHeader('X-Blob-Id')
          if (id) resolve(id)
          else reject(new Error('服务端没有返回 blobId'))
        },
        onError: (err) => reject(err),
      })
      // 同一文件刷新页面后接着传；查续传信息期间被取消 / 暂停就先不开始
      void upload.findPreviousUploads().then((prev) => {
        if (prev.length) upload!.resumeFromPreviousUpload(prev[0]!)
        ready = true
        startIfAllowed()
      })
    })
  })()

  return {
    done,
    abort: async () => {
      aborted = true
      await upload?.abort(true)
    },
    pause: () => {
      paused = true
      void upload?.abort(false)
    },
    resume: () => {
      paused = false
      startIfAllowed()
    },
  }
}

/** 取 blob 的同源 URL；v 为变体，r 用于缩略图生成后强制刷新 */
export function blobUrl(blobId: string, opts: { v?: string; name?: string; download?: boolean; r?: number } = {}): string {
  const q = new URLSearchParams()
  if (opts.v) q.set('v', opts.v)
  if (opts.name) q.set('name', opts.name)
  if (opts.download) q.set('download', 'true')
  if (opts.r) q.set('r', String(opts.r))
  const s = q.toString()
  return `/api/blobs/${blobId}${s ? `?${s}` : ''}`
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}
