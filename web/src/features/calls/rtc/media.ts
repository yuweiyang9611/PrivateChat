// 摄像头 / 麦克风 / 屏幕的采集。单独一个文件：calls store 的单测把它整个替换掉。

/** 通话需要安全上下文（HTTPS 或 localhost）和 WebRTC；局域网 http 直连时不可用 */
export function callsSupported(): boolean {
  return typeof window !== 'undefined' && window.isSecureContext && !!navigator.mediaDevices?.getUserMedia && 'RTCPeerConnection' in window
}

/** iOS Safari 没有 getDisplayMedia；安卓 Chrome 有但只能共享整个屏幕 */
export function screenShareSupported(): boolean {
  return callsSupported() && !!navigator.mediaDevices.getDisplayMedia
}

export type MediaFailure = 'denied' | 'not-found' | 'busy' | 'unsupported' | 'unknown'

/** getUserMedia / getDisplayMedia 失败的原因，决定给用户看哪句话 */
export function mediaFailure(e: unknown): MediaFailure {
  const name = (e as { name?: string } | null)?.name
  if (name === 'NotAllowedError' || name === 'SecurityError') return 'denied'
  if (name === 'NotFoundError' || name === 'OverconstrainedError') return 'not-found'
  if (name === 'NotReadableError' || name === 'AbortError') return 'busy'
  if (name === 'TypeError') return 'unsupported'
  return 'unknown'
}

export async function getMicrophone(): Promise<MediaStreamTrack> {
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true },
  })
  return stream.getAudioTracks()[0]!
}

export type Facing = 'user' | 'environment'

/** 720p / 24 帧：家庭通话够清楚，手机上行也扛得住 */
export async function getCamera(facing: Facing = 'user'): Promise<MediaStreamTrack> {
  const stream = await navigator.mediaDevices.getUserMedia({
    video: { width: { ideal: 1280 }, height: { ideal: 720 }, frameRate: { ideal: 24, max: 30 }, facingMode: facing },
  })
  return stream.getVideoTracks()[0]!
}

export async function getScreen(): Promise<MediaStreamTrack> {
  const stream = await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: { ideal: 15, max: 30 } }, audio: false })
  const track = stream.getVideoTracks()[0]!
  // 文字 / 界面优先保清晰度，而不是帧率
  try { track.contentHint = 'detail' } catch { /* 旧浏览器没有 contentHint */ }
  return track
}

/** 设备上是否有不止一个摄像头（决定显示不显示"翻转"按钮） */
export async function hasMultipleCameras(): Promise<boolean> {
  try {
    const devices = await navigator.mediaDevices.enumerateDevices()
    return devices.filter((d) => d.kind === 'videoinput').length > 1
  } catch {
    return false
  }
}

export interface PlaceholderVideo { track: MediaStreamTrack; stop(): void }

/**
 * 关摄像头时顶上去的黑画面轨道。
 * 不能直接 replaceTrack(null)：Cloudflare 30 秒收不到包就回收这条轨道，再开摄像头时对方已经订阅不到了；
 * 也不想只把 track.enabled 置 false：那样摄像头指示灯还亮着。所以真的停掉摄像头，换一条每秒几帧的黑画面占位。
 */
export function placeholderVideo(): PlaceholderVideo {
  const canvas = document.createElement('canvas')
  canvas.width = 320
  canvas.height = 180
  const ctx = canvas.getContext('2d')
  let tick = 0
  const draw = (): void => {
    if (!ctx) return
    ctx.fillStyle = '#000'
    ctx.fillRect(0, 0, canvas.width, canvas.height)
    // 画面完全不变时有的浏览器不出帧：每次动一个像素
    ctx.fillStyle = tick++ % 2 ? '#010101' : '#000'
    ctx.fillRect(0, 0, 1, 1)
  }
  draw()
  const timer = setInterval(draw, 500)
  const track = canvas.captureStream(2).getVideoTracks()[0]!
  return {
    track,
    stop() {
      clearInterval(timer)
      track.stop()
    },
  }
}

export function stopTrack(track: MediaStreamTrack | null | undefined): void {
  try { track?.stop() } catch { /* 已经停了 */ }
}
