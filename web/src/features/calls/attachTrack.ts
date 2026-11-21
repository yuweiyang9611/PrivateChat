/** 把一条视频轨道放进 video 元素（null = 清空）。同一条轨道不重复设置，否则画面会闪一下 */
export function attachTrack(el: HTMLVideoElement | null | undefined, track: MediaStreamTrack | null | undefined): void {
  if (!el) return
  const current = (el.srcObject as MediaStream | null)?.getVideoTracks()[0]
  if (current === (track ?? undefined)) return
  el.srcObject = track ? new MediaStream([track]) : null
  if (track) void el.play().catch(() => undefined)
}
