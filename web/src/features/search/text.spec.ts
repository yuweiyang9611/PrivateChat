import { describe, expect, it } from 'vitest'
import { highlight, matchesAll, snippet, termsOf } from './text'
import { matchLocal } from './local'
import type { LocalMessage } from '@/db'

describe('分词', () => {
  it('按空白和全角空格分开，去重，最多 5 个', () => {
    expect(termsOf('  饺子　公园 饺子 ')).toEqual(['饺子', '公园'])
    expect(termsOf('A a b')).toEqual(['A', 'b'])
    expect(termsOf('1 2 3 4 5 6 7')).toHaveLength(5)
    expect(termsOf('   ')).toEqual([])
  })
})

describe('高亮', () => {
  it('不区分大小写，长词优先，原文大小写保留', () => {
    expect(highlight('今天包饺子，Hello 饺子馆', ['饺子', 'hello'])).toEqual([
      { text: '今天包', hit: false },
      { text: '饺子', hit: true },
      { text: '，', hit: false },
      { text: 'Hello', hit: true },
      { text: ' ', hit: false },
      { text: '饺子', hit: true },
      { text: '馆', hit: false },
    ])
    expect(highlight('饺子馆', ['饺子', '饺子馆'])).toEqual([{ text: '饺子馆', hit: true }])
    expect(highlight('<b>x</b>', ['b'])[0]).toEqual({ text: '<', hit: false })
    expect(highlight('', ['a'])).toEqual([])
    expect(highlight('abc', [])).toEqual([{ text: 'abc', hit: false }])
  })
})

describe('摘要与匹配', () => {
  it('每个词都要出现', () => {
    expect(matchesAll('周末去公园包饺子', ['公园', '饺子'])).toBe(true)
    expect(matchesAll('周末去公园', ['公园', '饺子'])).toBe(false)
    expect(matchesAll(null, ['a'])).toBe(false)
  })

  it('长文本截出命中附近一段，不切开 emoji', () => {
    const long = '前'.repeat(80) + '关键词' + '后'.repeat(80)
    const s = snippet(long, ['关键词'])
    expect(s.startsWith('…') && s.endsWith('…')).toBe(true)
    expect(s).toContain('关键词')
    expect(snippet('短文本\n换行', ['短'])).toBe('短文本 换行')
    const emoji = 'a'.repeat(50) + '😀' + '关键' + 'b'.repeat(100)
    const e = snippet(emoji, ['关键'], 1)
    const first = e.replace(/^…/, '').charCodeAt(0)
    expect(first >= 0xdc00 && first <= 0xdfff).toBe(false)
  })

  it('离线搜索：文字消息、附件文件名，撤回和未发出的不算', () => {
    const base = { id: 'm1', conversationId: 'c1', seq: 3, senderId: 'u1', createdAt: '2026-11-14T08:00:00Z', clientMessageId: 'x', reactions: [], attachments: [], deleted: false, payload: null, replyToId: null, editedAt: null, status: 'sent' } as unknown as LocalMessage
    expect(matchLocal({ ...base, type: 'Text', body: '包饺子' } as LocalMessage, ['饺子'])).toMatchObject({ kind: 'text', snippet: '包饺子', seq: 3 })
    expect(matchLocal({ ...base, type: 'Text', body: '包饺子', deleted: true } as LocalMessage, ['饺子'])).toBeNull()
    expect(matchLocal({ ...base, type: 'Text', body: '包饺子', status: 'pending' } as LocalMessage, ['饺子'])).toBeNull()
    const file = { ...base, type: 'File', body: null, attachments: [{ id: 'a', blobId: 'b1', kind: 'File', meta: JSON.stringify({ name: '饺子做法.pdf', mime: 'application/pdf' }) }] } as unknown as LocalMessage
    expect(matchLocal(file, ['饺子'])).toMatchObject({ kind: 'file', fileName: '饺子做法.pdf', blobId: 'b1', attachmentKind: 'File' })
    expect(matchLocal({ ...base, type: 'Call', body: '视频通话 饺子' } as LocalMessage, ['饺子'])).toBeNull()
  })
})
