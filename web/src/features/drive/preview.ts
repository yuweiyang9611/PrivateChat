import { ref } from 'vue'
import { blobUrl } from '@/upload'
import { kindOfMime, triggerDownload } from './fileKind'

/** 能预览的一项（网盘文件或相册里的媒体） */
export interface PreviewItem {
  blobId: string
  name: string
  mime: string
}

/** 全局预览状态：图片走 Element Plus 图片查看器（可左右切换），音视频走对话框 */
export const imageViewer = ref<{ urls: string[]; index: number } | null>(null)
export const mediaDialog = ref<{ kind: 'video' | 'audio'; src: string; poster?: string; name: string; download: string } | null>(null)

/**
 * 打开预览：图片在同组里左右翻（只翻图片），视频 / 音频弹播放器，PDF 新标签页打开，其它直接下载。
 * siblings：同一个文件夹 / 时间线里的其它项，用于图片左右切换。
 */
export function openPreview(item: PreviewItem, siblings: PreviewItem[] = [item]): void {
  const kind = kindOfMime(item.mime)
  const download = blobUrl(item.blobId, { name: item.name, download: true })
  switch (kind) {
    case 'image': {
      const images = siblings.filter((s) => kindOfMime(s.mime) === 'image')
      const list = images.length ? images : [item]
      imageViewer.value = {
        urls: list.map((s) => blobUrl(s.blobId, { v: 'thumb-1280' })),
        index: Math.max(0, list.findIndex((s) => s.blobId === item.blobId)),
      }
      return
    }
    case 'video':
      mediaDialog.value = {
        kind: 'video',
        src: blobUrl(item.blobId, { v: 'play-720p' }),
        poster: blobUrl(item.blobId, { v: 'thumb-1280' }),
        name: item.name,
        download,
      }
      return
    case 'audio':
      mediaDialog.value = { kind: 'audio', src: blobUrl(item.blobId, { v: 'audio-m4a' }), name: item.name, download }
      return
    case 'pdf':
    case 'text':
      window.open(blobUrl(item.blobId, { name: item.name }), '_blank', 'noopener')
      return
    default:
      triggerDownload(download, item.name)
  }
}
