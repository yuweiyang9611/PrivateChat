import { afterEach, describe, expect, it, vi } from 'vitest'
import { newUuid } from './uuid'
import { classifyUploadError } from '@/stores/messages'

const V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/

describe('newUuid', () => {
  afterEach(() => vi.restoreAllMocks())

  it('有 randomUUID 时直接用', () => {
    expect(newUuid()).toMatch(V4)
  })

  it('局域网 http（非安全上下文）没有 randomUUID 时退回 getRandomValues，格式仍是合法的 v4', () => {
    const original = crypto.randomUUID
    Object.defineProperty(crypto, 'randomUUID', { value: undefined, configurable: true })
    try {
      const ids = new Set(Array.from({ length: 200 }, () => newUuid()))
      expect(ids.size).toBe(200)
      for (const id of ids) expect(id).toMatch(V4)
    } finally {
      Object.defineProperty(crypto, 'randomUUID', { value: original, configurable: true })
    }
  })
})

describe('classifyUploadError', () => {
  const http = (status: number) => Object.assign(new Error('tus'), { originalResponse: { getStatus: () => status } })

  it('连着服务器时 4xx / 5xx 是明确失败（408/409/423/429 除外）', () => {
    expect(classifyUploadError(http(413), true, true)).toBe('failed')
    expect(classifyUploadError(http(403), true, true)).toBe('failed')
    expect(classifyUploadError(http(500), true, true)).toBe('failed')
    for (const s of [408, 409, 423, 429]) expect(classifyUploadError(http(s), true, true)).toBe('retry')
  })

  it('断网 / 服务器连不上 / 没有响应都只是暂时问题', () => {
    expect(classifyUploadError(http(502), true, false)).toBe('retry')
    expect(classifyUploadError(http(500), false, true)).toBe('retry')
    expect(classifyUploadError(new TypeError('Failed to fetch'), true, true)).toBe('retry')
    expect(classifyUploadError(Object.assign(new Error('x'), { originalResponse: null }), true, true)).toBe('retry')
    expect(classifyUploadError(undefined, true, true)).toBe('retry')
  })

  it('本地文件读不了是明确失败', () => {
    expect(classifyUploadError(new DOMException('gone', 'NotFoundError'), false, false)).toBe('failed')
    expect(classifyUploadError(new DOMException('perm', 'NotReadableError'), true, true)).toBe('failed')
  })
})
