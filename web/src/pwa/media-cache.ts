// 应用页面与 Service Worker 共用：缩略图运行时缓存的规则。保持无依赖，sw.ts 也会打包它。

/** 缩略图缓存名；退出登录时页面直接删掉这个缓存 */
export const MEDIA_CACHE = 'pc-media-v1'

const BLOB_PATH = /^\/api\/blobs\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const THUMB_VARIANTS = new Set(['thumb-320', 'thumb-1280'])

/** 只缓存同源的缩略图请求：它们按 blob 内容寻址、生成后不再变，适合 CacheFirst */
export function isThumbRequest(url: URL, origin: string): boolean {
  return url.origin === origin && BLOB_PATH.test(url.pathname) && THUMB_VARIANTS.has(url.searchParams.get('v') ?? '')
}

/**
 * 缓存键去掉 r（前端在缩略图生成后递增的版本号，只为绕开浏览器 HTTP 缓存），
 * 这样刷新页面后（版本号归零）离线也能命中同一份缓存。
 */
export function mediaCacheKey(url: string): string {
  const u = new URL(url)
  u.searchParams.delete('r')
  return u.href
}

/**
 * 只缓存真正的缩略图：缩略图还没生成时服务器回退原图并带 no-store，这种响应不能进缓存，
 * 否则之后永远拿到的是原图。也不缓存 401 / 404 / 部分内容。
 */
export function shouldCacheMedia(status: number, cacheControl: string | null): boolean {
  if (status !== 200) return false
  return !/no-store/i.test(cacheControl ?? '')
}
