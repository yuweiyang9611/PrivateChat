import { describe, expect, it } from 'vitest'
import { gridColumns, groupReactions, monthTitle, relativeTime } from './format'

describe('动态的时间显示', () => {
  const now = new Date(2026, 10, 12, 15, 30)
  const at = (y: number, mo: number, d: number, h: number, mi = 0) => new Date(y, mo - 1, d, h, mi).toISOString()

  it('一分钟内是刚刚，一小时内按分钟', () => {
    expect(relativeTime(at(2026, 11, 12, 15, 29, ), now)).toBe('1 分钟前')
    expect(relativeTime(new Date(now.getTime() - 20_000).toISOString(), now)).toBe('刚刚')
  })

  it('今天按小时，昨天带时刻，今年带月日，往年只写日期', () => {
    expect(relativeTime(at(2026, 11, 12, 9), now)).toBe('6 小时前')
    expect(relativeTime(at(2026, 11, 11, 20, 5), now)).toBe('昨天 20:05')
    expect(relativeTime(at(2026, 3, 8, 7, 0), now)).toBe('3月8日 07:00')
    expect(relativeTime(at(2019, 11, 12, 10), now)).toBe('2019年11月12日')
  })
})

describe('点赞分组', () => {
  it('同一表情合并、保持先后顺序、同一人不重复', () => {
    expect(groupReactions([
      { userId: 'a', emoji: '❤️' },
      { userId: 'b', emoji: '👍' },
      { userId: 'c', emoji: '❤️' },
      { userId: 'a', emoji: '❤️' },
    ])).toEqual([{ emoji: '❤️', userIds: ['a', 'c'] }, { emoji: '👍', userIds: ['b'] }])
  })
})

describe('九宫格与按月相册', () => {
  it('列数', () => {
    expect([1, 2, 3, 4, 5, 9].map(gridColumns)).toEqual([1, 2, 3, 2, 3, 3])
  })

  it('月份标题', () => {
    expect(monthTitle('2026-03')).toBe('2026 年 3 月')
  })
})
