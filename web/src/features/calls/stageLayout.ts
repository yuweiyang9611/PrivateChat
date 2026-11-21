/**
 * 有人共享屏幕时怎么摆（纯计算，见 CallOverlay / ScreenStage）。
 * 共享的画面按它真实的比例尽量大；人像让到旁边（side）或下面（stack），看哪种摆法画面更大。
 */
export interface StageOpts {
  /** 格子之间的空隙 */
  gap: number
  /** stack 时画面上面那一行（谁的屏幕 + 全屏按钮）的高度 */
  capH: number
  /** side 时右边那一列最窄、最宽 */
  colMin: number
  colMax: number
  /** stack 时下面放人像的地方至少留这么高 */
  peopleMin: number
}

export interface StageFit {
  mode: 'side' | 'stack'
  /** 画面的宽高（px） */
  fw: number
  fh: number
  /** side 时右边那一列的宽度 */
  colW: number
}

/** W、H：能用的地方；ar：画面的宽高比 */
export function fitStage(W: number, H: number, ar: number, o: StageOpts): StageFit {
  if (!(W > 0) || !(H > 0) || !(ar > 0)) return { mode: 'stack', fw: 0, fh: 0, colW: 0 }
  // side：谁的屏幕写在右边那一列里，画面可以用满高度
  const sideW = Math.max(0, Math.min(W - o.colMin - o.gap, H * ar))
  // stack：上面一行字，中间画面，下面至少留给人像 peopleMin
  const stackW = Math.max(0, Math.min(W, (H - o.capH - o.peopleMin - 2 * o.gap) * ar))
  if (sideW >= stackW) {
    const fw = Math.floor(sideW)
    return { mode: 'side', fw, fh: Math.floor(fw / ar), colW: Math.floor(Math.min(o.colMax, Math.max(o.colMin, W - fw - o.gap))) }
  }
  const fw = Math.floor(stackW)
  return { mode: 'stack', fw, fh: Math.floor(fw / ar), colW: 0 }
}

/** 大图里按钮放右边还是下面：放在画面用不上的那一边（R：按钮那一条的厚度） */
export function bigViewSide(W: number, H: number, ar: number, R: number): 'right' | 'bottom' {
  return Math.min(W - R, H * ar) >= Math.min(W, (H - R) * ar) ? 'right' : 'bottom'
}

/** 放大 / 缩小按钮一步一步走的倍数 */
export const ZOOM_STEPS = [1, 1.5, 2, 3, 4]

/** 从 z 往上（dir = 1）或往下（-1）走到下一档；最大到 max，到头了就停在原地 */
export function nextZoom(z: number, dir: 1 | -1, max: number): number {
  const steps = ZOOM_STEPS.filter((s) => s <= max + 1e-9)
  const next = dir > 0 ? steps.find((s) => s > z + 0.01) : [...steps].reverse().find((s) => s < z - 0.01)
  return Math.min(max, Math.max(1, next ?? z))
}

/**
 * 放大缩小时让手指 / 鼠标下面的那一点不动：算新的滚动位置。
 * scroll：现在滚到哪；anchor：那一点在可视区域里的位置；box：可视区域的长度；oldSize / newSize：画面缩放前后的长度。
 * 画面比可视区域小时是居中放的（两边各空一半），这里也算进去。
 */
export function anchorScroll(scroll: number, anchor: number, box: number, oldSize: number, newSize: number): number {
  const o1 = Math.max(0, (box - oldSize) / 2)
  const o2 = Math.max(0, (box - newSize) / 2)
  return Math.max(0, ((scroll + anchor - o1) / oldSize) * newSize + o2 - anchor)
}
