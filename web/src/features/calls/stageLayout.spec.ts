import { describe, expect, it } from 'vitest'
import { anchorScroll, bigViewSide, fitStage, nextZoom } from './stageLayout'

const normal = { gap: 8, capH: 44, colMin: 160, colMax: 360, peopleMin: 120 }
const elder = { gap: 8, capH: 56, colMin: 200, colMax: 400, peopleMin: 150 }
const wide = 16 / 9

describe('共享屏幕怎么摆', () => {
  it('电脑上（宽屏）：画面用满高度，人像让到右边一列', () => {
    // 用户截图里的那种窗口：原来画面只有 1041×586
    expect(fitStage(1644, 740, wide, normal)).toEqual({ mode: 'side', fw: 1315, fh: 739, colW: 321 })
  })

  it('竖着的手机：画面用满宽度，人像放下面', () => {
    expect(fitStage(404, 700, wide, normal)).toEqual({ mode: 'stack', fw: 404, fh: 227, colW: 0 })
  })

  it('竖着的手机看别人共享的竖屏（手机屏幕）：画面放左边，右边一列最窄', () => {
    expect(fitStage(374, 603, 1080 / 2340, normal)).toEqual({ mode: 'side', fw: 206, fh: 446, colW: 160 })
  })

  it('长辈模式：右边一列宽一些（最宽 400），多出来的地方两边平分', () => {
    expect(fitStage(1346, 427, wide, elder)).toEqual({ mode: 'side', fw: 759, fh: 426, colW: 400 })
  })

  it('还没量到大小：什么都不摆', () => {
    expect(fitStage(0, 500, wide, normal)).toEqual({ mode: 'stack', fw: 0, fh: 0, colW: 0 })
    expect(fitStage(500, 500, Number.NaN, normal)).toEqual({ mode: 'stack', fw: 0, fh: 0, colW: 0 })
  })
})

describe('大图', () => {
  it('按钮放在画面用不上的那一边', () => {
    expect(bigViewSide(1920, 1080, wide, 80)).toBe('right')
    expect(bigViewSide(390, 630, wide, 80)).toBe('bottom')
  })

  it('放大缩小一档一档走，到头了停住', () => {
    expect(nextZoom(1, 1, 4)).toBe(1.5)
    expect(nextZoom(1.7, 1, 4)).toBe(2)
    expect(nextZoom(1.7, -1, 4)).toBe(1.5)
    expect(nextZoom(4, 1, 4)).toBe(4)
    expect(nextZoom(1, -1, 4)).toBe(1)
    // 画面本来就很大（最多只能放大 2.7 倍）：到 2 以后不再往上
    expect(nextZoom(2, 1, 2.7)).toBe(2)
  })

  it('放大时手指下面那一点不动', () => {
    expect(anchorScroll(0, 200, 400, 400, 600)).toBe(100)
    // 画面比可视区域小、居中放着：从正中间放大，还是正中间
    expect(anchorScroll(0, 200, 400, 200, 400)).toBe(0)
    expect(anchorScroll(0, 200, 400, 200, 800)).toBe(200)
  })
})
