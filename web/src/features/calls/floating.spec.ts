import { describe, expect, it } from 'vitest'
import { clampInto, cornerStyle, nearestCorner } from './floating'

describe('浮窗', () => {
  it('松手时贴到最近的角（看浮窗中心在哪一半）', () => {
    expect(nearestCorner(10, 10, 100, 80, 1000, 600)).toBe('tl')
    expect(nearestCorner(800, 20, 100, 80, 1000, 600)).toBe('tr')
    expect(nearestCorner(20, 500, 100, 80, 1000, 600)).toBe('bl')
    expect(nearestCorner(890, 510, 100, 80, 1000, 600)).toBe('br')
    // 中心刚过一半
    expect(nearestCorner(451, 261, 100, 80, 1000, 600)).toBe('br')
  })

  it('拖的时候不出容器', () => {
    expect(clampInto(-50, -20, 100, 80, 1000, 600)).toEqual({ x: 0, y: 0 })
    expect(clampInto(980, 590, 100, 80, 1000, 600)).toEqual({ x: 900, y: 520 })
    // 容器比浮窗还小：贴着左上
    expect(clampInto(30, 30, 100, 80, 60, 40)).toEqual({ x: 0, y: 0 })
  })

  it('停在角上时的定位', () => {
    const inset = { top: '1px', right: '2px', bottom: '3px', left: '4px' }
    expect(cornerStyle('tl', inset)).toEqual({ top: '1px', left: '4px' })
    expect(cornerStyle('br', inset)).toEqual({ bottom: '3px', right: '2px' })
  })
})
