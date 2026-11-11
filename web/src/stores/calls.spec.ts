// 通话 store 的行为测试：真实的 Pinia store，信令接口 / WebRTC 连接 / 摄像头麦克风 / 铃声用可控的假实现。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { CallDto, ParticipantDto } from '@/features/calls/api'

const ME = 'user-me'
const DAD = 'user-dad'
const MOM = 'user-mom'
const CONV = 'conv-1'

interface FakeTrack { kind: string; label: string; enabled: boolean; stopped: boolean; stop(): void; addEventListener(type: string, cb: () => void): void; end(): void }
interface Ref { participantId: string; trackName: string; kind: string }
interface Handlers { onRemoteTrack(r: Ref, t: unknown): void; onRemoteTrackGone(r: Ref): void; onConnectionLost(reason: string): void; onState?(s: string): void }
interface FakeSession {
  ice: unknown
  handlers: Handlers
  published: { kind: string; track: FakeTrack }[]
  replaced: { kind: string; track: FakeTrack | null }[]
  unpublished: string[]
  subscriptions: Ref[]
  closed: boolean
}

const ctl = vi.hoisted(() => ({
  config: { enabled: true, ringSeconds: 60, heartbeatSeconds: 10 },
  active: [] as unknown[],
  rooms: [] as unknown[],
  /** callId → 服务器上的通话；get / join 都读它 */
  server: {} as Record<string, unknown>,
  joins: [] as string[],
  joinErrors: [] as number[],
  leaves: [] as string[],
  declines: [] as string[],
  states: [] as [string, boolean, boolean][],
  heartbeats: 0,
  heartbeatError: 0,
  starts: [] as [string, string][],
  sessions: [] as unknown[],
  publishErrors: [] as number[],
  micError: null as string | null,
  camError: null as string | null,
  tracks: [] as unknown[],
  ringtone: [] as string[],
  participantSeq: 0,
  supported: true,
  createdConversations: [] as unknown[],
}))

vi.mock('@/realtime/connection', async () => {
  const { ref } = await import('vue')
  return { connectionState: ref(0), isConnected: () => true, startConnection: async () => undefined, stopConnection: async () => undefined }
})
vi.mock('@/push', () => ({ disablePush: async () => undefined }))
vi.mock('@/api/client', () => ({
  api: {
    GET: async () => ({ data: [], response: { ok: true } }),
    POST: async (path: string, init: { body: unknown }) => {
      if (path === '/api/conversations') {
        ctl.createdConversations.push(init.body)
        return { data: { id: CONV, type: 'Direct', title: null, memberIds: [ME, DAD] }, response: { ok: true } }
      }
      throw new TypeError('Failed to fetch')
    },
  },
  fetchMe: async () => ({ probe: 'offline' }),
  reauthenticate: () => false,
  setUnauthorizedHandler: () => undefined,
  handleAuthLoss: () => undefined,
}))

vi.mock('@/features/calls/rtc/RealtimeSession', () => {
  class SignalingError extends Error {
    status: number
    constructor(status: number, message: string) {
      super(message)
      this.status = status
    }
  }
  class RealtimeSession {
    ice: unknown
    handlers: Handlers
    published: { kind: string; track: FakeTrack }[] = []
    replaced: { kind: string; track: FakeTrack | null }[] = []
    unpublished: string[] = []
    subscriptions: Ref[] = []
    closed = false
    constructor(ice: unknown, _signaling: unknown, handlers: Handlers) {
      this.ice = ice
      this.handlers = handlers
      ctl.sessions.push(this)
    }
    async publish(items: { kind: string; track: FakeTrack }[]): Promise<void> {
      const status = ctl.publishErrors.shift()
      if (status) {
        // 和真实实现一样：转发前就被拒绝的只抛错，其余还会报"连接不可用"
        if (![400, 403, 409].includes(status)) this.handlers.onConnectionLost('publish')
        throw new SignalingError(status, 'publish failed')
      }
      for (const i of items) if (!this.published.some((p) => p.kind === i.kind)) this.published.push(i)
    }
    async replaceTrack(kind: string, track: FakeTrack | null): Promise<boolean> {
      if (!this.published.some((p) => p.kind === kind)) return false
      this.replaced.push({ kind, track })
      return true
    }
    isPublished(kind: string): boolean { return this.published.some((p) => p.kind === kind) }
    async unpublish(kind: string): Promise<void> {
      this.unpublished.push(kind)
      this.published = this.published.filter((p) => p.kind !== kind)
    }
    setSubscriptions(refs: Ref[]): void { this.subscriptions = refs }
    close(): void { this.closed = true }
  }
  return { RealtimeSession, SignalingError }
})

vi.mock('@/features/calls/rtc/media', () => {
  const make = (kind: string, label: string): FakeTrack => {
    const listeners: (() => void)[] = []
    const t: FakeTrack = {
      kind, label, enabled: true, stopped: false,
      stop() { t.stopped = true },
      addEventListener(_type, cb) { listeners.push(cb) },
      end() { for (const cb of listeners) cb() },
    }
    ctl.tracks.push(t)
    return t
  }
  const fail = (name: string): never => { throw Object.assign(new Error(name), { name }) }
  return {
    callsSupported: () => ctl.supported,
    screenShareSupported: () => true,
    hasMultipleCameras: async () => false,
    mediaFailure: (e: { name?: string }) => (e?.name === 'NotAllowedError' ? 'denied' : e?.name === 'NotFoundError' ? 'not-found' : 'unknown'),
    getMicrophone: async () => (ctl.micError ? fail(ctl.micError) : make('audio', 'mic')),
    getCamera: async (facing = 'user') => (ctl.camError ? fail(ctl.camError) : make('video', `camera-${facing}`)),
    getScreen: async () => make('video', 'screen'),
    placeholderVideo: () => {
      const track = make('video', 'placeholder')
      return { track, stop: () => track.stop() }
    },
    stopTrack: (t?: FakeTrack | null) => t?.stop(),
  }
})

vi.mock('@/features/calls/ringtone', () => ({
  startRingtone: (kind: string) => { ctl.ringtone.push(kind) },
  stopRingtone: () => { ctl.ringtone.push('stop') },
}))

vi.mock('@/features/calls/api', async () => {
  const { SignalingError } = await import('@/features/calls/rtc/RealtimeSession')
  const clone = <T>(v: T): T => JSON.parse(JSON.stringify(v)) as T
  return {
    callsApi: {
      config: async () => ctl.config,
      active: async () => clone(ctl.active),
      rooms: async () => clone(ctl.rooms),
      history: async () => [],
      usage: async () => ({}),
      get: async (id: string) => {
        if (!ctl.server[id]) throw new SignalingError(404, 'not found')
        return clone(ctl.server[id])
      },
      start: async (conversationId: string, type: string) => {
        ctl.starts.push([conversationId, type])
        const c = call('call-new', { conversationId, type: type as CallDto['type'], startedBy: ME })
        ctl.server[c.id] = c
        return clone(c)
      },
      join: async (id: string) => {
        const status = ctl.joinErrors.shift()
        if (status) throw new SignalingError(status, 'join failed')
        ctl.joins.push(id)
        const c = ctl.server[id] as unknown as CallDto
        const participantId = `p-me-${++ctl.participantSeq}`
        c.participants = [...c.participants.filter((p) => p.userId !== ME), participant(participantId, ME)]
        if (!c.roomSlug && !c.answeredAt && c.participants.length >= 2) c.answeredAt = new Date().toISOString()
        return { participantId, iceServers: [{ urls: ['stun:stun.example:3478'] }], call: clone(c) }
      },
      decline: async (id: string) => { ctl.declines.push(id) },
      end: async () => undefined,
      heartbeat: async () => {
        ctl.heartbeats++
        if (ctl.heartbeatError) throw new SignalingError(ctl.heartbeatError, 'gone')
      },
      setState: async (id: string, micOn: boolean, camOn: boolean) => { ctl.states.push([id, micOn, camOn]) },
      leave: async (id: string) => { ctl.leaves.push(id) },
      leaveBeacon: (id: string) => { ctl.leaves.push(id) },
      signaling: () => ({}),
    },
  }
})

function participant(id: string, userId: string, tracks: ParticipantDto['tracks'] = [], extra: Partial<ParticipantDto> = {}): ParticipantDto {
  return { id, userId, micOn: true, camOn: tracks.some((t) => t.kind === 'Video'), joinedAt: new Date().toISOString(), tracks, ...extra }
}

function call(id: string, extra: Partial<CallDto> = {}): CallDto {
  return {
    id, conversationId: CONV, type: 'Video', roomSlug: null, startedBy: DAD,
    startedAt: new Date().toISOString(), answeredAt: null, endedAt: null, endReason: null,
    participants: [], joinedUserIds: [],
    ...extra,
  }
}

const DAD_TRACKS: ParticipantDto['tracks'] = [{ trackName: 'audio-dad', kind: 'Audio' }, { trackName: 'video-dad', kind: 'Video' }]

/** 爸爸打来的视频电话：他已经在里面并且发布了音视频 */
function incomingFromDad(id = 'call-1') {
  const c = call(id, { participants: [participant('p-dad', DAD, DAD_TRACKS)], joinedUserIds: [DAD] })
  ctl.server[id] = c
  return c
}

async function boot() {
  vi.resetModules()
  const { createPinia, setActivePinia } = await import('pinia')
  setActivePinia(createPinia())
  const { useSessionStore } = await import('@/stores/session')
  const { useConversationsStore } = await import('@/stores/conversations')
  const { useCallsStore } = await import('@/stores/calls')
  useSessionStore().user = { id: ME, email: 'me@example.com', username: 'me', displayName: '我', role: 'Member', elderMode: false, avatarBlobId: null, authScheme: 'Local' }
  const conversations = useConversationsStore()
  conversations.items = [{ id: CONV, type: 'Direct', title: null, avatarBlobId: null, lastSeq: 0, lastReadSeq: 0, unread: 0, mutedUntil: null, memberIds: [ME, DAD] }]
  const store = useCallsStore()
  await store.init()
  return { store, conversations }
}

const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms))
const sessions = () => ctl.sessions as FakeSession[]
const tracks = () => ctl.tracks as FakeTrack[]
const lastSession = () => sessions()[sessions().length - 1]!

beforeEach(() => {
  Object.assign(ctl, {
    config: { enabled: true, ringSeconds: 60, heartbeatSeconds: 10 },
    active: [], rooms: [], server: {}, joins: [], joinErrors: [], leaves: [], declines: [], states: [], heartbeats: 0, heartbeatError: 0,
    starts: [], sessions: [], publishErrors: [], micError: null, camError: null, tracks: [], ringtone: [], participantSeq: 0, supported: true,
    createdConversations: [],
  })
  localStorage.clear()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('来电', () => {
  it('别人打来的会响铃；自己发起的、已经结束的不响', async () => {
    const { store } = await boot()
    store.onIncomingCall(call('mine', { startedBy: ME }))
    expect(store.incoming).toBeNull()

    const c = incomingFromDad()
    store.onIncomingCall(c)
    expect(store.incoming?.id).toBe('call-1')
    expect(ctl.ringtone).toEqual(['incoming'])

    // 响铃超时 / 对方取消：服务器发来已结束，弹窗和铃声都收掉
    store.onCallUpdated({ ...c, endedAt: new Date().toISOString(), endReason: 'Missed' } as never)
    expect(store.incoming).toBeNull()
    expect(ctl.ringtone.at(-1)).toBe('stop')
    // 结束的通话从"进行中"里拿掉；我自己发起的那通还在
    expect(store.ongoing.map((x) => x.id)).toEqual(['mine'])
  })

  it('重新打开页面时，还在响的来电照样弹出来；响铃已经超时的不弹', async () => {
    ctl.active = [incomingFromDad()]
    const { store } = await boot()
    expect(store.incoming?.id).toBe('call-1')

    ctl.active = [call('old', { startedAt: new Date(Date.now() - 70_000).toISOString() })]
    const again = await boot()
    expect(again.store.incoming).toBeNull()
    // 但仍然是一通可以加入的通话
    expect(again.store.ongoingIn(CONV)?.id).toBe('old')
  })

  it('静音的会话不响铃，只显示可以加入', async () => {
    const { store, conversations } = await boot()
    conversations.items[0]!.mutedUntil = new Date(Date.now() + 3600_000).toISOString()
    store.onIncomingCall(incomingFromDad())
    expect(store.incoming).toBeNull()
    expect(ctl.ringtone).toEqual([])
    expect(store.ongoingIn(CONV)?.id).toBe('call-1')
  })

  it('拒接：通知服务器，之后重连刷新也不再响', async () => {
    const { store } = await boot()
    const c = incomingFromDad()
    store.onIncomingCall(c)
    store.decline()
    expect(store.incoming).toBeNull()
    expect(ctl.declines).toEqual(['call-1'])
    ctl.active = [c]
    await store.refresh()
    expect(store.incoming).toBeNull()
  })

  it('在另一台设备上接听或拒接了，这边不再响', async () => {
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    store.onParticipantJoined({ callId: 'call-1', participant: participant('p-me-phone', ME) as never })
    expect(store.incoming).toBeNull()

    store.onIncomingCall(incomingFromDad('call-2'))
    expect(store.incoming?.id).toBe('call-2')
    store.onCallDeclined({ callId: 'call-2', userId: ME })
    expect(store.incoming).toBeNull()
  })
})

describe('接听与通话中', () => {
  it('接听：拿麦克风和摄像头 → 加入 → 发布自己的 → 订阅对方已发布的轨道', async () => {
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    expect(await store.accept()).toBe(true)

    expect(ctl.joins).toEqual(['call-1'])
    expect(store.incoming).toBeNull()
    expect(store.current).toMatchObject({ callId: 'call-1', participantId: 'p-me-1', phase: 'active', micOn: true, camOn: true })
    const s = lastSession()
    expect(s.ice).toEqual([{ urls: ['stun:stun.example:3478'] }])
    expect(s.published.map((p) => [p.kind, p.track.label])).toEqual([['Audio', 'mic'], ['Video', 'camera-user']])
    expect(s.subscriptions).toEqual([
      { participantId: 'p-dad', trackName: 'audio-dad', kind: 'Audio' },
      { participantId: 'p-dad', trackName: 'video-dad', kind: 'Video' },
    ])
    expect(store.localVideo).toBe(tracks()[1])
    // 铃停了，没有呼出的回铃音
    expect(ctl.ringtone).toEqual(['incoming', 'stop', 'stop'])

    // 订阅到的轨道按参与者归位；对方取消发布后清掉
    s.handlers.onRemoteTrack({ participantId: 'p-dad', trackName: 'video-dad', kind: 'Video' }, 'VIDEO')
    s.handlers.onRemoteTrack({ participantId: 'p-dad', trackName: 'audio-dad', kind: 'Audio' }, 'AUDIO')
    expect(store.remote['p-dad']).toEqual({ video: 'VIDEO', audio: 'AUDIO' })
    store.onTrackUnpublished({ callId: 'call-1', participantId: 'p-dad', trackName: 'video-dad', kind: 'Video' })
    expect(s.subscriptions).toEqual([{ participantId: 'p-dad', trackName: 'audio-dad', kind: 'Audio' }])
    s.handlers.onRemoteTrackGone({ participantId: 'p-dad', trackName: 'video-dad', kind: 'Video' })
    expect(store.remote['p-dad']).toEqual({ audio: 'AUDIO' })
  })

  it('麦克风没给权限：不加入，来电窗留着可以再试', async () => {
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    ctl.micError = 'NotAllowedError'
    expect(await store.accept()).toBe(false)
    expect(ctl.joins).toEqual([])
    expect(store.current).toBeNull()
    expect(store.incoming?.id).toBe('call-1')
    expect(store.notice).toMatchObject({ key: 'calls.micDenied', type: 'error' })

    ctl.micError = null
    expect(await store.accept()).toBe(true)
    expect(store.incoming).toBeNull()
  })

  it('摄像头打不开：退成只有语音，照样接通', async () => {
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    ctl.camError = 'NotReadableError'
    expect(await store.accept()).toBe(true)
    expect(store.current).toMatchObject({ camOn: false })
    expect(lastSession().published.map((p) => p.kind)).toEqual(['Audio'])
    expect(store.notice?.key).toBe('calls.camFailed')
  })

  it('接听时对方已经挂了：提示并收掉来电窗', async () => {
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    ctl.joinErrors = [410]
    expect(await store.accept()).toBe(false)
    expect(store.incoming).toBeNull()
    expect(store.notice?.key).toBe('calls.alreadyEnded')
    // 已经拿到的麦克风、摄像头要释放
    expect(tracks().every((t) => t.stopped)).toBe(true)
  })

  it('不支持（局域网 http）或没配置时不发起', async () => {
    const { store } = await boot()
    ctl.supported = false
    expect(await store.startCall(CONV, 'Audio')).toBe(false)
    expect(store.notice?.key).toBe('calls.unsupported')
    ctl.supported = true
    store.config!.enabled = false
    expect(await store.startCall(CONV, 'Audio')).toBe(false)
    expect(store.notice?.key).toBe('calls.notConfigured')
    expect(ctl.starts).toEqual([])
  })
})

describe('呼出', () => {
  it('发起 → 回铃音 → 对方接听后进入通话并订阅对方', async () => {
    const { store } = await boot()
    expect(await store.startCall(CONV, 'Audio')).toBe(true)
    expect(ctl.starts).toEqual([[CONV, 'Audio']])
    expect(store.current).toMatchObject({ callId: 'call-new', phase: 'outgoing', camOn: false })
    expect(ctl.ringtone.at(-1)).toBe('outgoing')
    // 语音通话不开摄像头
    expect(lastSession().published.map((p) => p.kind)).toEqual(['Audio'])
    expect(lastSession().subscriptions).toEqual([])

    // 对方接听：先是有人加入，再是通话"已接通"，然后对方发布轨道
    store.onParticipantJoined({ callId: 'call-new', participant: participant('p-dad', DAD) as never })
    store.onCallUpdated({ ...store.currentCall!, answeredAt: new Date().toISOString() })
    expect(store.current?.phase).toBe('active')
    expect(ctl.ringtone.at(-1)).toBe('stop')
    store.onTrackPublished({ callId: 'call-new', participantId: 'p-dad', trackName: 'audio-dad', kind: 'Audio' })
    expect(lastSession().subscriptions).toEqual([{ participantId: 'p-dad', trackName: 'audio-dad', kind: 'Audio' }])
    expect(store.others.map((p) => p.userId)).toEqual([DAD])

    // 对方静音：只更新图标状态，轨道不动
    store.onParticipantUpdated({ callId: 'call-new', participant: participant('p-dad', DAD, [], { micOn: false }) as never })
    expect(store.others[0]).toMatchObject({ micOn: false })
    expect(store.others[0]!.tracks).toHaveLength(1)
  })

  it('对方拒接 / 无人接听 / 正常结束：收场并说明原因', async () => {
    for (const [reason, key] of [['Declined', 'calls.declinedByPeer'], ['Missed', 'calls.noAnswer']] as const) {
      const { store } = await boot()
      await store.startCall(CONV, 'Video')
      store.onCallUpdated({ ...store.currentCall!, endedAt: new Date().toISOString(), endReason: reason })
      expect(store.current).toBeNull()
      expect(store.notice?.key).toBe(key)
      expect(lastSession().closed).toBe(true)
      expect(tracks().every((t) => t.stopped)).toBe(true)
      expect(store.ongoing).toEqual([])
      // 服务器已经结束了通话：不用再发离开
      expect(ctl.leaves).toEqual([])
      Object.assign(ctl, { sessions: [], tracks: [], server: {} })
    }
  })

  it('给家人打电话：先建（或取回）单聊，再在里面发起', async () => {
    const { store } = await boot()
    expect(await store.callMember(DAD, 'Video')).toBe(true)
    expect(ctl.createdConversations).toEqual([{ type: 'Direct', title: null, memberIds: [DAD] }])
    expect(ctl.starts).toEqual([[CONV, 'Video']])
  })

  it('挂断：通知服务器、关连接、停采集', async () => {
    const { store } = await boot()
    await store.startCall(CONV, 'Video')
    await store.hangup()
    expect(ctl.leaves).toEqual(['call-new'])
    expect(store.current).toBeNull()
    expect(store.localVideo).toBeNull()
    expect(lastSession().closed).toBe(true)
    expect(tracks().every((t) => t.stopped)).toBe(true)
    expect(ctl.ringtone.at(-1)).toBe('stop')
  })

  it('通话中又来电话：接听会先挂断当前这通', async () => {
    const { store } = await boot()
    await store.startCall(CONV, 'Audio')
    const first = lastSession()
    store.onIncomingCall(call('call-mom', { conversationId: 'conv-mom', startedBy: MOM, participants: [participant('p-mom', MOM)] }))
    ctl.server['call-mom'] = call('call-mom', { conversationId: 'conv-mom', startedBy: MOM, participants: [participant('p-mom', MOM)] })
    expect(store.incoming?.id).toBe('call-mom')
    // 正在通话：只弹窗，不响铃盖过通话
    expect(ctl.ringtone.filter((r) => r === 'incoming')).toEqual([])

    expect(await store.accept()).toBe(true)
    expect(ctl.leaves).toEqual(['call-new'])
    expect(first.closed).toBe(true)
    expect(store.current?.callId).toBe('call-mom')
    expect(ctl.joins).toEqual(['call-new', 'call-mom'])
  })
})

describe('开关', () => {
  it('静音只停用轨道并上报状态', async () => {
    const { store } = await boot()
    await store.startCall(CONV, 'Video')
    store.toggleMic()
    expect(tracks()[0]).toMatchObject({ label: 'mic', enabled: false, stopped: false })
    expect(store.current?.micOn).toBe(false)
    expect(ctl.states.at(-1)).toEqual(['call-new', false, true])
    store.toggleMic()
    expect(tracks()[0]!.enabled).toBe(true)
  })

  it('关摄像头：真的停掉摄像头，发布的轨道换成黑画面占位；再打开换回新的摄像头轨道', async () => {
    const { store } = await boot()
    await store.startCall(CONV, 'Video')
    const s = lastSession()
    const camera = tracks()[1]!
    await store.toggleCam()
    expect(camera.stopped).toBe(true)
    expect(store.localVideo).toBeNull()
    expect(s.replaced.map((r) => r.track?.label)).toEqual(['placeholder'])
    expect(ctl.states.at(-1)).toEqual(['call-new', true, false])
    // 没有重新协商
    expect(s.published.map((p) => p.kind)).toEqual(['Audio', 'Video'])

    await store.toggleCam()
    expect(s.replaced.map((r) => r.track?.label)).toEqual(['placeholder', 'camera-user'])
    expect(store.localVideo?.label).toBe('camera-user')
    expect(tracks().find((t) => t.label === 'placeholder')!.stopped).toBe(true)
    expect(ctl.states.at(-1)).toEqual(['call-new', true, true])
  })

  it('语音通话里第一次开摄像头：新发布一条视频轨道', async () => {
    const { store } = await boot()
    await store.startCall(CONV, 'Audio')
    await store.toggleCam()
    expect(lastSession().published.map((p) => p.kind)).toEqual(['Audio', 'Video'])
    expect(store.current?.camOn).toBe(true)
  })

  it('共享屏幕：发布第三条轨道；点浏览器自带的"停止共享"也会取消发布', async () => {
    const { store } = await boot()
    await store.startCall(CONV, 'Video')
    const s = lastSession()
    await store.toggleScreen()
    expect(s.published.map((p) => p.kind)).toEqual(['Audio', 'Video', 'Screen'])
    expect(store.current?.screenOn).toBe(true)
    expect(store.localScreen?.label).toBe('screen')

    tracks().find((t) => t.label === 'screen')!.end()
    await settle()
    expect(s.unpublished).toEqual(['Screen'])
    expect(store.current?.screenOn).toBe(false)
    expect(store.localScreen).toBeNull()
  })

  it('共享屏幕被服务器拒绝：撤回本地状态并提示', async () => {
    const { store } = await boot()
    await store.startCall(CONV, 'Video')
    ctl.publishErrors = [409]
    await store.toggleScreen()
    expect(store.current?.screenOn).toBe(false)
    expect(tracks().find((t) => t.label === 'screen')!.stopped).toBe(true)
    expect(store.notice?.key).toBe('calls.screenFailed')
    expect(sessions()).toHaveLength(1)
  })
})

describe('断线与重连', () => {
  it('媒体连接断了：重新加入换新连接，本地轨道接着用，状态照旧', async () => {
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    await store.accept()
    store.toggleMic()
    const first = lastSession()
    first.handlers.onRemoteTrack({ participantId: 'p-dad', trackName: 'audio-dad', kind: 'Audio' }, 'AUDIO')

    first.handlers.onConnectionLost('ice-failed')
    expect(store.current?.phase).toBe('reconnecting')
    await settle(5)
    expect(first.closed).toBe(true)
    expect(sessions()).toHaveLength(2)
    expect(ctl.joins).toEqual(['call-1', 'call-1'])
    expect(store.current).toMatchObject({ participantId: 'p-me-2', phase: 'active', micOn: false })
    const second = lastSession()
    // 同一条麦克风、摄像头轨道（没有重新申请权限）
    expect(second.published.map((p) => p.track)).toEqual(first.published.map((p) => p.track))
    expect(second.subscriptions).toHaveLength(2)
    expect(tracks().filter((t) => t.label === 'mic')).toHaveLength(1)
    // 新的参与者默认"麦克风开"，要把静音状态重新告诉服务器
    expect(ctl.states.at(-1)).toEqual(['call-1', false, true])
    // 旧连接上的画面已经无效
    expect(store.remote).toEqual({})
  })

  it('重连时服务器广播"旧身份被顶掉"（可能比 join 的响应先到）：不当成是别的设备接听了', async () => {
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    await store.accept()
    const first = lastSession()
    // join 的响应还没回来，服务器已经把旧参与者标成 replaced 广播出来
    first.handlers.onConnectionLost('ice-failed')
    store.onParticipantLeft({ callId: 'call-1', participantId: 'p-me-1', userId: ME, reason: 'replaced' })
    await settle(5)
    expect(store.current).toMatchObject({ participantId: 'p-me-2', phase: 'active' })
    expect(store.notice?.key).not.toBe('calls.replaced')
    expect(ctl.leaves).toEqual([])
    // 真的被别的设备顶掉（不是自己重连）仍然收场
    store.onParticipantLeft({ callId: 'call-1', participantId: 'p-me-2', userId: ME, reason: 'replaced' })
    expect(store.current).toBeNull()
    expect(store.notice?.key).toBe('calls.replaced')
  })

  it('重连时通话已经结束：直接收场', async () => {
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    await store.accept()
    ctl.joinErrors = [410]
    lastSession().handlers.onConnectionLost('ice-failed')
    await settle(5)
    expect(store.current).toBeNull()
    expect(store.notice?.key).toBe('calls.ended')
    expect(tracks().every((t) => t.stopped)).toBe(true)
  })

  it('第一次发布就失败（Cloudflare 报错）：自动重连一次后恢复', async () => {
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    ctl.publishErrors = [502]
    expect(await store.accept()).toBe(true)
    await settle(5)
    expect(sessions()).toHaveLength(2)
    expect(sessions()[0]!.closed).toBe(true)
    expect(store.current?.phase).toBe('active')
    expect(lastSession().published.map((p) => p.kind)).toEqual(['Audio', 'Video'])
  })

  it('被别的设备顶掉：本地收场，不通知服务器', async () => {
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    await store.accept()
    store.onParticipantLeft({ callId: 'call-1', participantId: 'p-me-1', userId: ME, reason: 'replaced' })
    expect(store.current).toBeNull()
    expect(store.notice?.key).toBe('calls.replaced')
    expect(ctl.leaves).toEqual([])
    expect(ctl.joins).toEqual(['call-1'])
  })

  it('被服务器按掉线清理（心跳 410）：对账后重新加入', async () => {
    vi.useFakeTimers()
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    await store.accept()
    await vi.advanceTimersByTimeAsync(10_000)
    expect(ctl.heartbeats).toBe(1)
    expect(sessions()).toHaveLength(1)

    // 服务器已经把我清掉了
    const server = ctl.server['call-1'] as unknown as CallDto
    server.participants = server.participants.filter((p) => p.userId !== ME)
    ctl.heartbeatError = 410
    await vi.advanceTimersByTimeAsync(10_000)
    ctl.heartbeatError = 0
    await vi.advanceTimersByTimeAsync(50)
    expect(ctl.joins).toEqual(['call-1', 'call-1'])
    expect(store.current).toMatchObject({ participantId: 'p-me-2', phase: 'active' })
  })

  it('别人离开：清掉他的画面和订阅', async () => {
    const { store } = await boot()
    store.onIncomingCall(incomingFromDad())
    await store.accept()
    const s = lastSession()
    s.handlers.onRemoteTrack({ participantId: 'p-dad', trackName: 'video-dad', kind: 'Video' }, 'VIDEO')
    store.onParticipantLeft({ callId: 'call-1', participantId: 'p-dad', userId: DAD, reason: 'left' })
    expect(store.remote).toEqual({})
    expect(s.subscriptions).toEqual([])
    expect(store.others).toEqual([])
    // 我还在通话里（群聊 / 客厅里别人走了不影响我）
    expect(store.current?.callId).toBe('call-1')
  })
})

describe('客厅与通知入口', () => {
  it('客厅不响铃；进去之后直接是通话中，离开后房间还在', async () => {
    const room = call('room-1', { conversationId: null, roomSlug: 'living-room', type: 'Meeting', startedBy: DAD, participants: [participant('p-dad', DAD, DAD_TRACKS)] })
    ctl.rooms = [room]
    ctl.server['room-1'] = room
    const { store } = await boot()
    expect(store.incoming).toBeNull()
    expect(store.rooms.map((r) => r.id)).toEqual(['room-1'])
    expect(store.ongoing).toEqual([])

    expect(await store.join('room-1')).toBe(true)
    expect(store.current).toMatchObject({ phase: 'active', camOn: true })
    expect(lastSession().subscriptions).toHaveLength(2)
    await store.hangup()
    expect(store.rooms.map((r) => r.id)).toEqual(['room-1'])
    expect(store.rooms[0]!.participants.map((p) => p.userId)).toEqual([DAD])
  })

  it('从通知点"接听"进来：直接接；只是点开通知则弹来电窗', async () => {
    const { store } = await boot()
    incomingFromDad()
    await store.openFromLink('call-1')
    expect(store.incoming?.id).toBe('call-1')
    expect(store.current).toBeNull()

    await store.openFromLink('call-1', 'answer')
    expect(store.current?.callId).toBe('call-1')
    expect(store.incoming).toBeNull()

    // 通知点晚了，通话已经结束
    ctl.server['call-9'] = call('call-9', { endedAt: new Date().toISOString(), endReason: 'Missed' })
    await store.openFromLink('call-9', 'answer')
    expect(store.notice?.key).toBe('calls.alreadyEnded')
    expect(store.current?.callId).toBe('call-1')
  })
})
