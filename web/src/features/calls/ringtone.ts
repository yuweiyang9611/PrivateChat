// 铃声：用 Web Audio 现合成，不带音频文件（离线也能响，也不占预缓存）。
// 浏览器要求页面被用户点过之后才能出声；从没点过的页面只会静音响铃，界面照常弹出。

type Pattern = { tones: { freq: number; at: number; len: number }[]; period: number; gain: number }

/** 来电：两声一组的上扬铃声；呼出：国内回铃音的节奏（响 1 秒停 4 秒），音量小一些 */
const PATTERNS: Record<'incoming' | 'outgoing', Pattern> = {
  incoming: { tones: [{ freq: 784, at: 0, len: 0.22 }, { freq: 988, at: 0.26, len: 0.3 }, { freq: 784, at: 0.8, len: 0.22 }, { freq: 988, at: 1.06, len: 0.3 }], period: 3, gain: 0.25 },
  outgoing: { tones: [{ freq: 450, at: 0, len: 1 }], period: 5, gain: 0.08 },
}

let ctx: AudioContext | null = null
let timer: ReturnType<typeof setInterval> | undefined
let vibrateTimer: ReturnType<typeof setInterval> | undefined
let current: 'incoming' | 'outgoing' | null = null

function playOnce(audio: AudioContext, p: Pattern): void {
  const t0 = audio.currentTime + 0.05
  for (const tone of p.tones) {
    const osc = audio.createOscillator()
    const gain = audio.createGain()
    osc.type = 'sine'
    osc.frequency.value = tone.freq
    // 起止各 20 ms 的淡入淡出，避免"哒"的一声
    gain.gain.setValueAtTime(0, t0 + tone.at)
    gain.gain.linearRampToValueAtTime(p.gain, t0 + tone.at + 0.02)
    gain.gain.setValueAtTime(p.gain, t0 + tone.at + tone.len - 0.02)
    gain.gain.linearRampToValueAtTime(0, t0 + tone.at + tone.len)
    osc.connect(gain).connect(audio.destination)
    osc.start(t0 + tone.at)
    osc.stop(t0 + tone.at + tone.len + 0.02)
  }
}

export function startRingtone(kind: 'incoming' | 'outgoing'): void {
  if (current === kind) return
  stopRingtone()
  current = kind
  try {
    const Ctor = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext
    if (Ctor) {
      ctx ??= new Ctor()
      const audio = ctx
      const p = PATTERNS[kind]
      const ring = (): void => { if (current === kind && audio.state === 'running') playOnce(audio, p) }
      // resume 是异步的：进入 running 之后再响第一声
      void audio.resume().then(ring, () => undefined)
      timer = setInterval(ring, p.period * 1000)
    }
  } catch {
    // 没有声音也不影响接听
  }
  if (kind === 'incoming' && typeof navigator.vibrate === 'function') {
    const buzz = (): void => { try { navigator.vibrate([400, 200, 400]) } catch { /* 桌面浏览器 */ } }
    buzz()
    vibrateTimer = setInterval(buzz, 3000)
  }
}

export function stopRingtone(): void {
  if (!current) return
  current = null
  clearInterval(timer)
  clearInterval(vibrateTimer)
  timer = undefined
  vibrateTimer = undefined
  try { if (typeof navigator.vibrate === 'function') navigator.vibrate(0) } catch { /* ignore */ }
  // 挂起而不是关闭：下次来电直接复用，不用再等用户手势
  void ctx?.suspend().catch(() => undefined)
}
