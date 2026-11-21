import { getCurrentScope, onScopeDispose, ref, type Ref } from 'vue'

/** 浮窗停在哪个角：t / b = 上 / 下，l / r = 左 / 右 */
export type Corner = 'tl' | 'tr' | 'bl' | 'br'

export const isCorner = (v: unknown): v is Corner => v === 'tl' || v === 'tr' || v === 'bl' || v === 'br'

/** 松手时贴到最近的角。(x, y)：浮窗左上角在容器里的位置；w × h：浮窗；W × H：容器 */
export function nearestCorner(x: number, y: number, w: number, h: number, W: number, H: number): Corner {
  const top = y + h / 2 < H / 2
  const left = x + w / 2 < W / 2
  return `${top ? 't' : 'b'}${left ? 'l' : 'r'}`
}

/** 拖的时候不出容器 */
export function clampInto(x: number, y: number, w: number, h: number, W: number, H: number): { x: number; y: number } {
  return { x: Math.min(Math.max(0, x), Math.max(0, W - w)), y: Math.min(Math.max(0, y), Math.max(0, H - h)) }
}

/** 离四边多远（CSS 长度，可以带 env(safe-area-inset-*)） */
export interface Insets { top: string; right: string; bottom: string; left: string }

/** 停在某个角时的定位 */
export function cornerStyle(c: Corner, inset: Insets): Record<string, string> {
  return c[0] === 't'
    ? { top: inset.top, ...(c[1] === 'l' ? { left: inset.left } : { right: inset.right }) }
    : { bottom: inset.bottom, ...(c[1] === 'l' ? { left: inset.left } : { right: inset.right }) }
}

/**
 * 能拖着走、松手贴到最近一个角的浮窗。
 * container：以哪个元素为界（浮窗是它里面 position: absolute 的）；不传就以整个窗口为界（浮窗是 position: fixed 的）。
 * 手指 / 鼠标动了 6 px 以上才算拖：只是点一下的话，里面的按钮照常能点；拖完松手时浏览器补的那个 click 不算数（clicked() 为 false）。
 * 按下以后在整个窗口上跟着指针（不抓指针：抓了的话 click 会落到浮窗本身，里面的按钮点不到）：
 * 鼠标一下子挪出浮窗、在别处松开，也照样跟得上、收得了尾。
 */
export function useFloatDrag(el: Ref<HTMLElement | null>, corner: Ref<Corner>, container?: Ref<HTMLElement | null>) {
  /** 正在拖：浮窗左上角在容器里的位置；没在拖是 null（按 corner 贴角） */
  const drag = ref<{ x: number; y: number } | null>(null)
  let start: { id: number; px: number; py: number; x: number; y: number; moved: boolean } | null = null
  let justDragged = false
  let userSelect: string | null = null

  function bounds(): { left: number; top: number; width: number; height: number } {
    const box = container?.value?.getBoundingClientRect()
    return box ?? { left: 0, top: 0, width: window.innerWidth, height: window.innerHeight }
  }

  function listen(on: boolean): void {
    if (on) {
      window.addEventListener('pointermove', onMove, true)
      window.addEventListener('pointerup', onUp, true)
      window.addEventListener('pointercancel', onUp, true)
    } else {
      window.removeEventListener('pointermove', onMove, true)
      window.removeEventListener('pointerup', onUp, true)
      window.removeEventListener('pointercancel', onUp, true)
    }
  }

  /** 拖的时候别把后面页面上的字选中 */
  function noSelect(on: boolean): void {
    const style = document.documentElement.style
    if (on && userSelect === null) {
      userSelect = style.userSelect
      style.userSelect = 'none'
      document.getSelection()?.removeAllRanges()
    } else if (!on && userSelect !== null) {
      style.userSelect = userSelect
      userSelect = null
    }
  }

  function onPointerdown(e: PointerEvent): void {
    if (e.pointerType === 'mouse' && e.button !== 0) return
    const node = el.value
    if (!node || start) return
    const r = node.getBoundingClientRect()
    const b = bounds()
    start = { id: e.pointerId, px: e.clientX, py: e.clientY, x: r.left - b.left, y: r.top - b.top, moved: false }
    listen(true)
  }

  function onMove(e: PointerEvent): void {
    const node = el.value
    if (!start || e.pointerId !== start.id || !node) return
    const dx = e.clientX - start.px
    const dy = e.clientY - start.py
    if (!start.moved) {
      // 手指点一下本来就会挪几像素（手机认"点"的范围大约 8–10 px）：手指挪 12 px 以上才算拖，鼠标 6 px
      if (Math.hypot(dx, dy) <= (e.pointerType === 'mouse' ? 6 : 12)) return
      start.moved = true
      noSelect(true)
    }
    const b = bounds()
    drag.value = clampInto(start.x + dx, start.y + dy, node.offsetWidth, node.offsetHeight, b.width, b.height)
  }

  function onUp(e: PointerEvent): void {
    if (!start || e.pointerId !== start.id) return
    const moved = start.moved
    start = null
    listen(false)
    noSelect(false)
    if (!moved) return
    const node = el.value
    const at = drag.value
    if (node && at && e.type === 'pointerup') {
      const b = bounds()
      corner.value = nearestCorner(at.x, at.y, node.offsetWidth, node.offsetHeight, b.width, b.height)
    }
    drag.value = null
    justDragged = true
    setTimeout(() => { justDragged = false }, 0)
  }

  // 拖到一半浮窗没了（通话结束、退出大图）：把窗口上的监听收掉
  if (getCurrentScope()) {
    onScopeDispose(() => {
      start = null
      listen(false)
      noSelect(false)
    })
  }

  return {
    drag,
    handlers: { pointerdown: onPointerdown },
    /** 这次 click 是真的点了一下（不是拖完松手） */
    clicked: () => !justDragged,
  }
}
