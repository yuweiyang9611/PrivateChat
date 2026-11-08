export type FileKind = 'folder' | 'image' | 'video' | 'audio' | 'pdf' | 'text' | 'archive' | 'doc' | 'other'

export function kindOfMime(mime: string | null | undefined, isFolder = false): FileKind {
  if (isFolder) return 'folder'
  const m = (mime ?? '').toLowerCase()
  if (m.startsWith('image/') && m !== 'image/svg+xml') return 'image'
  if (m.startsWith('video/')) return 'video'
  if (m.startsWith('audio/')) return 'audio'
  if (m === 'application/pdf') return 'pdf'
  if (m.startsWith('text/')) return 'text'
  if (/zip|rar|7z|tar|gzip|compressed/.test(m)) return 'archive'
  if (/word|excel|spreadsheet|presentation|powerpoint|officedocument|opendocument/.test(m)) return 'doc'
  return 'other'
}

/** 有缩略图的类型（图片原图缩放 / 视频抽帧） */
export function hasThumb(kind: FileKind): boolean {
  return kind === 'image' || kind === 'video'
}

export const KIND_EMOJI: Record<FileKind, string> = {
  folder: '📁', image: '🖼️', video: '🎞️', audio: '🎵', pdf: '📕', text: '📄', archive: '🗜️', doc: '📘', other: '📄',
}

export function formatBytes(bytes: number): string {
  if (!bytes) return '0 B'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(0)} KB`
  if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MB`
  if (bytes < 1024 ** 4) return `${(bytes / 1024 ** 3).toFixed(2)} GB`
  return `${(bytes / 1024 ** 4).toFixed(2)} TB`
}

export function formatDuration(seconds: number | null | undefined): string {
  if (!seconds || seconds < 0) return ''
  const s = Math.round(seconds)
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const r = String(s % 60).padStart(2, '0')
  return h ? `${h}:${String(m).padStart(2, '0')}:${r}` : `${m}:${r}`
}

export function formatDate(iso: string): string {
  const d = new Date(iso)
  const now = new Date()
  if (d.toDateString() === now.toDateString()) return d.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
  if (d.getFullYear() === now.getFullYear()) return d.toLocaleDateString('zh-CN', { month: 'numeric', day: 'numeric' })
  return d.toLocaleDateString('zh-CN', { year: 'numeric', month: 'numeric', day: 'numeric' })
}

/** 时间线按月分组的标题 */
export function monthLabel(iso: string): string {
  const d = new Date(iso)
  return `${d.getFullYear()} 年 ${d.getMonth() + 1} 月`
}

/** 浏览器触发下载（同源链接，Cookie 自动带上） */
export function triggerDownload(url: string, name: string): void {
  const a = document.createElement('a')
  a.href = url
  a.download = name
  a.rel = 'noopener'
  document.body.appendChild(a)
  a.click()
  a.remove()
}
