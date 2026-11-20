import { describe, expect, it } from 'vitest'
import { choiceFor, effectiveElder, ELDER_KEY, LEGACY_ELDER_KEY, migrateLegacy, parseChoice } from './elder'

function memory(initial: Record<string, string> = {}) {
  const data = new Map(Object.entries(initial))
  return {
    data,
    getItem: (k: string) => data.get(k) ?? null,
    setItem: (k: string, v: string) => void data.set(k, v),
    removeItem: (k: string) => void data.delete(k),
  }
}

describe('长辈模式：账号上的开关和这台设备上的选择', () => {
  it('设备上没拨过：跟账号', () => {
    expect(effectiveElder(null, true)).toBe(true)
    expect(effectiveElder(null, false)).toBe(false)
  })

  it('设备上拨过：以设备为准', () => {
    expect(effectiveElder('off', true)).toBe(false)
    expect(effectiveElder('on', false)).toBe(true)
  })

  it('拨到和账号一样：不单独记，以后跟着账号变', () => {
    expect(choiceFor(true, true)).toBeNull()
    expect(choiceFor(false, false)).toBeNull()
  })

  it('拨到和账号不一样：记成这台设备的选择', () => {
    expect(choiceFor(false, true)).toBe('off')
    expect(choiceFor(true, false)).toBe('on')
  })

  it('存的值认不出来就当没拨过', () => {
    expect(parseChoice('on')).toBe('on')
    expect(parseChoice('off')).toBe('off')
    expect(parseChoice('')).toBeNull()
    expect(parseChoice('true')).toBeNull()
    expect(parseChoice(null)).toBeNull()
  })
})

describe('长辈模式：老版本存的值', () => {
  it('以前开着：算这台设备自己开的', () => {
    const s = memory({ [LEGACY_ELDER_KEY]: 'true' })
    migrateLegacy(s)
    expect(s.data.get(ELDER_KEY)).toBe('on')
    expect(s.data.has(LEGACY_ELDER_KEY)).toBe(false)
  })

  it('以前是 false（多半是当初写进去的默认值）：当成没拨过，跟账号', () => {
    const s = memory({ [LEGACY_ELDER_KEY]: 'false' })
    migrateLegacy(s)
    expect(s.data.has(ELDER_KEY)).toBe(false)
    expect(s.data.has(LEGACY_ELDER_KEY)).toBe(false)
  })

  it('已经有新的值：不覆盖', () => {
    const s = memory({ [LEGACY_ELDER_KEY]: 'true', [ELDER_KEY]: 'off' })
    migrateLegacy(s)
    expect(s.data.get(ELDER_KEY)).toBe('off')
  })

  it('没有老的值：什么都不动', () => {
    const s = memory({ [ELDER_KEY]: 'on' })
    migrateLegacy(s)
    expect([...s.data.entries()]).toEqual([[ELDER_KEY, 'on']])
  })
})
