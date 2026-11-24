import { createApp, nextTick } from 'vue'
import ElementPlus from 'element-plus'
import { expect, it, vi } from 'vitest'
import { i18n } from '@/i18n'
import { Features, FrameType, StatusCode } from './protocol'
import type { RemoteClientEvents } from './RemoteClient'
import RemoteView from './RemoteView.vue'

let events: RemoteClientEvents
const send = vi.fn()
const setAudio = vi.fn((on: boolean) => events.audio(on ? 'playing' : 'off'))
const end = vi.fn()
vi.mock('@/stores/remote', () => ({ useRemoteStore: () => ({
  sessions: { demo: { mode: 'Control', state: 'Active' } }, get: vi.fn(), end,
}) }))
vi.mock('@/stores/family', () => ({ useFamilyStore: () => ({ load: vi.fn() }) }))
vi.mock('vue-router', () => ({ useRouter: () => ({}), onBeforeRouteLeave: vi.fn() }))
vi.mock('./RemoteAudio', () => ({ audioSupported: () => true }))
vi.mock('./RemoteClient', () => ({
  webCodecsSupported: () => true,
  RemoteClient: class {
    constructor(_id: string, _canvas: HTMLCanvasElement, on: RemoteClientEvents) { events = on }
    connect() { events.state('streaming') }
    send = send
    setAudio = setAudio
    setVideo() {}
    setTransport() {}
    resumeAudio() {}
    close() {}
  },
}))

it('安全桌面上暂停正在排队和新来的输入，仍可静音、结束，普通桌面回来后才恢复输入', async () => {
  const root = document.body.appendChild(document.createElement('div'))
  const app = createApp(RemoteView, { id: 'demo' }).use(i18n).use(ElementPlus)
  const byId = (id: string) => root.querySelector<HTMLElement>(`[data-testid="${id}"]`)
  try {
    app.mount(root)
    await nextTick()
    events.config({ width: 640, height: 480, fps: 30, monitor: 0, features: Features.Audio })
    await nextTick()
    const stage = byId('remote-stage')!
    const canvas = byId('remote-canvas')!
    const clearRect = vi.fn()
    Object.defineProperty(canvas, 'getContext', { value: () => ({ clearRect }) })
    const key = (code: string, ctrlKey = false) => stage.dispatchEvent(new KeyboardEvent('keydown', { code, ctrlKey }))
    key('KeyV', true) // paste 事件还没到，页面上有一条待发送的按键
    events.status(StatusCode.SecureDesktop, '')
    events.status(StatusCode.CaptureFailed, '')
    await nextTick()

    expect(byId('remote-blocked')?.textContent).toContain('远程画面与操作已暂停')
    expect(canvas.style.display).toBe('none')
    expect(byId('remote-type')).toBeNull()
    key('KeyA')
    canvas.dispatchEvent(new WheelEvent('wheel', { deltaY: 120 }))
    stage.focus()
    const paste = new Event('paste', { cancelable: true })
    Object.defineProperty(paste, 'clipboardData', { value: { getData: () => '本机文字' } })
    document.dispatchEvent(paste)
    byId('remote-sound')!.click()
    byId('remote-end')!.click()
    expect(setAudio.mock.calls).toEqual([[true], [false]])
    expect(end).toHaveBeenCalledWith('demo')

    events.status(StatusCode.Ok, '')
    await nextTick()
    await new Promise((resolve) => setTimeout(resolve, 175)) // 恢复后也不能补发旧的 Ctrl+V
    expect(send).not.toHaveBeenCalled()
    expect(byId('remote-blocked')).toBeNull()
    expect(canvas.style.display).not.toBe('none')
    expect(clearRect).toHaveBeenCalledOnce()
    key('KeyA')
    expect(send.mock.calls[0]?.[0][0]).toBe(FrameType.Key)
  } finally {
    app.unmount()
    root.remove()
    localStorage.removeItem('remote.sound')
  }
})
