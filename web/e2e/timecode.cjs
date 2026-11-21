// "忙碌的测试画面"（被控端 --source busy）左上角有一排黑白方块，是采集时刻的时间码。
// 网页把画面画到 canvas 上之后读回时间码，和自己的时钟一比就是端到端延迟（两边在同一台电脑上，同一个时钟）；
// 不同的时间码一秒出现几个就是真正看到的帧率。两条线路的画面都画在同一块 canvas 上，读法一样。
//
// 读法：在这块 canvas 的 drawImage 后面挂一个钩子，每画一帧就读一次。
// 以前是每 10 ms 去读一次 canvas：读一次要从显卡拷回一整行，两次之间常常隔二三十毫秒，
// 30 帧/秒时两帧之间只有 33 ms，夹在两次读之间画上又被盖掉的帧就数不到，帧率会少算一大截（实测 26 帧只数到 14）。

/** 收集 seconds 秒：每个新时间码记下它是什么时候画出来的、延迟多少 */
const sample = (page, seconds) => page.evaluate(async (seconds) => {
  const canvas = document.querySelector('[data-testid=remote-canvas]')
  const ctx = canvas.getContext('2d')
  const BITS = 20
  const MASK = (1 << BITS) - 1
  const read = () => {
    if (!canvas.width) return null
    // 一次读回整行
    const row = ctx.getImageData(0, Math.floor(24 / 720 * canvas.height), canvas.width, 1).data
    let bits = 0
    for (let i = 0; i < BITS + 4; i++) {
      const o = Math.floor((8 + i * 36 + 16) / 1280 * canvas.width) * 4
      if (row[o] + row[o + 1] + row[o + 2] > 384) bits |= 1 << i
    }
    const stamp = bits & MASK
    let sum = 0
    for (let i = 0; i < BITS; i += 4) sum += (stamp >> i) & 0xf
    return (sum & 0xf) === (bits >>> BITS) ? stamp : null // 校验不过：这一帧糊得读不出来
  }
  const frames = []
  let last = -1
  let unreadable = 0
  const seen = (now) => {
    const stamp = read()
    if (stamp === null) {
      unreadable++
      return
    }
    if (stamp === last) return
    last = stamp
    const latency = (((Math.floor(now / 10) & MASK) - stamp) & MASK) * 10
    if (latency < 120_000) frames.push({ at: now, latency })
  }
  // 只钩这一块 canvas 的 drawImage（挂在这个 context 对象上，不动原型）；量完摘掉
  const original = ctx.drawImage
  ctx.drawImage = function (...args) {
    const r = original.apply(this, args)
    try { seen(Date.now()) } catch { /* 读不了就算了 */ }
    return r
  }
  try {
    await new Promise((r) => setTimeout(r, seconds * 1000))
  } finally {
    delete ctx.drawImage
  }
  return { frames, unreadable }
}, seconds)

function summarize({ frames, unreadable }, seconds) {
  if (frames.length === 0) return { frames: 0, unreadable }
  const sorted = frames.map((f) => f.latency).sort((a, b) => a - b)
  const at = (p) => sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * p))]
  let longestFreeze = 0
  for (let i = 1; i < frames.length; i++) longestFreeze = Math.max(longestFreeze, frames[i].at - frames[i - 1].at)
  return { fps: +(frames.length / seconds).toFixed(1), median: at(0.5), p90: at(0.9), max: sorted[sorted.length - 1], longestFreeze, unreadable }
}

module.exports = { sample, summarize }
