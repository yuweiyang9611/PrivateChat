import { describe, expect, it } from 'vitest'
import { isThumbRequest, mediaCacheKey, shouldCacheMedia } from './media-cache'

const origin = 'https://chat.example.com'
const id = '01a1b16b-878a-71e6-ad23-984e05e67dec'

describe('isThumbRequest', () => {
  it('只匹配同源的两档缩略图', () => {
    expect(isThumbRequest(new URL(`${origin}/api/blobs/${id}?v=thumb-320`), origin)).toBe(true)
    expect(isThumbRequest(new URL(`${origin}/api/blobs/${id}?v=thumb-1280&r=2`), origin)).toBe(true)
  })

  it('原图、语音、其它变体、跨域、带后缀路径都不缓存', () => {
    expect(isThumbRequest(new URL(`${origin}/api/blobs/${id}`), origin)).toBe(false)
    expect(isThumbRequest(new URL(`${origin}/api/blobs/${id}?v=audio-m4a`), origin)).toBe(false)
    expect(isThumbRequest(new URL(`${origin}/api/blobs/${id}?v=poster`), origin)).toBe(false)
    expect(isThumbRequest(new URL(`https://evil.example/api/blobs/${id}?v=thumb-320`), origin)).toBe(false)
    expect(isThumbRequest(new URL(`${origin}/api/blobs/${id}/x?v=thumb-320`), origin)).toBe(false)
    expect(isThumbRequest(new URL(`${origin}/api/blobs/precheck?v=thumb-320`), origin)).toBe(false)
  })
})

describe('mediaCacheKey', () => {
  it('去掉版本号 r，保留其它参数', () => {
    expect(mediaCacheKey(`${origin}/api/blobs/${id}?v=thumb-320&r=3`)).toBe(`${origin}/api/blobs/${id}?v=thumb-320`)
    expect(mediaCacheKey(`${origin}/api/blobs/${id}?r=1&v=thumb-1280`)).toBe(`${origin}/api/blobs/${id}?v=thumb-1280`)
    expect(mediaCacheKey(`${origin}/api/blobs/${id}?v=thumb-320`)).toBe(`${origin}/api/blobs/${id}?v=thumb-320`)
  })
})

describe('shouldCacheMedia', () => {
  it('只缓存 200 且不是 no-store 的响应', () => {
    expect(shouldCacheMedia(200, 'private, max-age=31536000, immutable')).toBe(true)
    expect(shouldCacheMedia(200, null)).toBe(true)
    // 缩略图未生成时服务器回退原图并带 no-store
    expect(shouldCacheMedia(200, 'no-store')).toBe(false)
    expect(shouldCacheMedia(200, 'private, No-Store')).toBe(false)
    expect(shouldCacheMedia(206, 'private, max-age=31536000, immutable')).toBe(false)
    expect(shouldCacheMedia(401, null)).toBe(false)
    expect(shouldCacheMedia(404, null)).toBe(false)
  })
})
