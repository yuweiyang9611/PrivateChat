/**
 * 长辈模式开不开，由两处决定：
 * - 账号上的开关：管理员在"家庭"页替某位家人打开（长辈自己不用找设置），跟着账号走，换设备也在；
 * - 这台设备上的选择：在"设置"里自己拨过开关，只管这台设备，而且比账号上的优先
 *   （比如爸爸的账号开着，但他在电脑上不想要大字）。
 * 设备上没拨过（null）就跟账号。
 */
export type ElderChoice = 'on' | 'off' | null

export const ELDER_KEY = 'pc.elder'
/** 老版本只有设备上的一个布尔值，而且会把默认的 false 也写进去 */
export const LEGACY_ELDER_KEY = 'pc.elderMode'

export function effectiveElder(device: ElderChoice, account: boolean): boolean {
  return device === null ? account : device === 'on'
}

/** 在设置里把开关拨到 want：和账号上的一样就不用单独记（以后跟着账号变），不一样才记成这台设备的选择 */
export function choiceFor(want: boolean, account: boolean): ElderChoice {
  return want === account ? null : want ? 'on' : 'off'
}

export function parseChoice(raw: string | null | undefined): ElderChoice {
  return raw === 'on' || raw === 'off' ? raw : null
}

/**
 * 把老版本存的值搬过来：true 是用户自己开的，算这台设备的选择；
 * false 分不清是用户关的还是当初写进去的默认值，当成没拨过（那时也没有账号上的开关可以冲突）。
 */
export function migrateLegacy(storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>): void {
  const legacy = storage.getItem(LEGACY_ELDER_KEY)
  if (legacy === null) return
  storage.removeItem(LEGACY_ELDER_KEY)
  if (legacy === 'true' && storage.getItem(ELDER_KEY) === null) storage.setItem(ELDER_KEY, 'on')
}
