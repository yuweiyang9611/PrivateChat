import { defineStore } from 'pinia'
import { computed, ref, shallowRef } from 'vue'
import { api } from '@/api/client'
import { callsApi, isReplaced, type CallConfigDto, type CallDto, type CallType, type ParticipantDto } from '@/features/calls/api'
import { RealtimeSession, SignalingError, type IceServerConfig, type MediaState, type RemoteTrackRef, type TrackKind } from '@/features/calls/rtc/RealtimeSession'
import {
  callsSupported, getCamera, getMicrophone, getScreen, mediaFailure, placeholderVideo, stopTrack,
  type Facing, type PlaceholderVideo,
} from '@/features/calls/rtc/media'
import { startRingtone, stopRingtone } from '@/features/calls/ringtone'
import { useConversationsStore } from './conversations'
import { useDriveUploadsStore } from './driveUploads'
import { useSessionStore } from './session'

/** outgoing：已进入、等对方接听；active：通话中；reconnecting：媒体断了，正在换新连接 */
export type CallPhase = 'outgoing' | 'active' | 'reconnecting'

export interface CurrentCall {
  callId: string
  participantId: string
  phase: CallPhase
  micOn: boolean
  camOn: boolean
  screenOn: boolean
  /** 缩成右下角悬浮条，可以继续看聊天 */
  minimized: boolean
  mediaState: MediaState
}

export interface RemoteMedia { audio?: MediaStreamTrack; video?: MediaStreamTrack; screen?: MediaStreamTrack }
export interface CallNotice { key: string; params?: Record<string, string>; type: 'info' | 'warning' | 'error'; id: number }

export interface ParticipantEvent { callId: string; participant: ParticipantDto }
export interface ParticipantLeftEvent { callId: string; participantId: string; userId: string; reason: string }
export interface TrackEvent { callId: string; participantId: string; trackName: string; kind: TrackKind }
export interface CallDeclinedEvent { callId: string; userId: string }

const SLOT: Record<TrackKind, keyof RemoteMedia> = { Audio: 'audio', Video: 'video', Screen: 'screen' }
/** 重连：每次重新加入之间的等待 */
export const RESTART_DELAYS_MS = [0, 1500, 4000, 8000]
/** 连着重连这么多轮都没真正连通过，就放弃 */
const MAX_RESTART_ROUNDS = 4
/** 切到后台超过这么久回来：系统多半暂停过采集，Cloudflare 30 秒收不到媒体会回收发布的轨道，要重新发布 */
const BACKGROUND_REPUBLISH_MS = 25_000
const delay = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms))

/**
 * 通话状态（docs/DESIGN.md 4.5 / 5.3）：
 * - calls：已知的进行中通话与会议室（服务器快照 + SignalR 事件增量维护），聊天页的"加入通话"、通话页的客厅都读它；
 * - incomingId：正在响铃的来电；current：我正在其中的那一通，连同本地媒体与 RealtimeSession。
 * 媒体对象（轨道、连接）不放进响应式状态，只把界面要用的轨道引用放在 shallowRef 里。
 */
export const useCallsStore = defineStore('calls', () => {
  const config = ref<CallConfigDto | null>(null)
  const calls = ref<Record<string, CallDto>>({})
  const incomingId = ref<string | null>(null)
  const current = ref<CurrentCall | null>(null)
  /** participantId → 订阅到的远端轨道 */
  const remote = shallowRef<Record<string, RemoteMedia>>({})
  const localVideo = shallowRef<MediaStreamTrack | null>(null)
  const localScreen = shallowRef<MediaStreamTrack | null>(null)
  const notice = ref<CallNotice | null>(null)
  /** 正在申请权限 / 加入中：按钮置灰，避免连点 */
  const entering = ref(false)
  /** 正在发起（建单聊、建通话）：和 entering 一起防连点，免得点两下建出两通通话 */
  const starting = ref(false)
  const busy = computed(() => entering.value || starting.value)
  /** 正在开关 / 切换摄像头：getUserMedia 要等一会儿，连点会多开一个摄像头且关不掉 */
  const camBusy = ref(false)

  // ---- 不进响应式系统的资源 ----
  let session: RealtimeSession | null = null
  let mic: MediaStreamTrack | null = null
  let cam: MediaStreamTrack | null = null
  let screen: MediaStreamTrack | null = null
  let placeholder: PlaceholderVideo | null = null
  let facing: Facing = 'user'
  let heartbeatTimer: ReturnType<typeof setInterval> | undefined
  let ringTimer: ReturnType<typeof setTimeout> | undefined
  let wakeLock: { release(): Promise<void> } | null = null
  /** 每次进入 / 离开通话 +1：异步步骤回来时发现变了就作废 */
  let generation = 0
  let restarting = false
  let sessionLost = false
  let restartRounds = 0
  let noticeSeq = 0
  /** 页面切到后台的时间，以及后台期间采集是否被系统暂停过 */
  let hiddenAt = 0
  let mutedWhileHidden = false
  /** 上一次心跳定时器真的跑起来的时间：页面被冻结（iOS、安卓把后台页面挂起）时它会停 */
  let lastBeat = 0
  let stalledWhileHidden = false
  /** 已经拒接 / 忽略过的来电，重连刷新时不再响 */
  const dismissed = new Set<string>()
  /** 重连时被我自己顶掉的旧参与者：服务器会广播它"被顶掉"，别当成是别的设备接听了 */
  const selfReplaced = new Set<string>()

  /** 排障用：通话问题多半只在特定网络 / 浏览器上出现，留一行日志比事后猜强 */
  function log(message: string, error?: unknown): void {
    if (error !== undefined) console.warn(`[calls] ${message}`, error)
    else console.warn(`[calls] ${message}`)
  }

  const me = (): string | undefined => useSessionStore().user?.id
  const enabled = computed(() => !!config.value?.enabled)
  const incoming = computed(() => (incomingId.value ? calls.value[incomingId.value] ?? null : null))
  const currentCall = computed(() => (current.value ? calls.value[current.value.callId] ?? null : null))
  const rooms = computed(() => Object.values(calls.value).filter((c) => c.roomSlug))
  /** 进行中、可以加入的通话（不含会议室） */
  const ongoing = computed(() => Object.values(calls.value).filter((c) => !c.roomSlug && !c.endedAt))
  /** 当前通话里除我之外的参与者 */
  const others = computed<ParticipantDto[]>(() => {
    const c = current.value
    return (currentCall.value?.participants ?? []).filter((p) => p.id !== c?.participantId && p.userId !== me())
  })

  function closeSession(): void {
    session?.close()
    session = null
  }

  function notify(key: string, type: CallNotice['type'] = 'info', params?: Record<string, string>): void {
    notice.value = { key, type, params, id: ++noticeSeq }
  }

  function ongoingIn(conversationId: string): CallDto | undefined {
    return ongoing.value.find((c) => c.conversationId === conversationId)
  }

  // ---------------------------------------------------------------- 快照与来电

  /** 登录后调用一次 */
  async function init(): Promise<void> {
    try {
      config.value = await callsApi.config()
    } catch {
      // 离线启动：连上后 refresh 里再取
    }
    await refresh()
  }

  /** 每次连上（含重连）、手机切回前台：和服务器对一遍进行中的通话、会议室和我自己的通话 */
  async function refresh(): Promise<void> {
    try {
      config.value ??= await callsApi.config()
      const [active, roomList] = await Promise.all([callsApi.active(), callsApi.rooms()])
      const next: Record<string, CallDto> = {}
      for (const c of [...active, ...roomList]) next[c.id] = c
      // 我正在其中的通话可能不在列表里（刚结束）：留给下面的对账处理
      const cur = current.value
      if (cur && !next[cur.callId] && calls.value[cur.callId]) next[cur.callId] = calls.value[cur.callId]!
      calls.value = next

      if (incomingId.value && !isRinging(next[incomingId.value])) clearIncoming()
      const ringing = active.find((c) => shouldRing(c))
      if (ringing && !incomingId.value) ring(ringing)
      if (cur) await reconcileCurrent()
    } catch {
      // 服务器不通：保持现状，下次连上再对
    }
  }

  function ringSeconds(): number {
    return config.value?.ringSeconds ?? 60
  }

  function isRinging(c: CallDto | undefined): boolean {
    return !!c && !c.endedAt && !c.answeredAt && !c.roomSlug && Date.now() - new Date(c.startedAt).getTime() < ringSeconds() * 1000
  }

  function shouldRing(c: CallDto): boolean {
    const uid = me()
    if (!isRinging(c) || c.startedBy === uid || dismissed.has(c.id) || current.value?.callId === c.id) return false
    if (c.participants.some((p) => p.userId === uid)) return false
    // 静音的会话不响铃（聊天页里仍然能看到"加入通话"）
    const muted = c.conversationId ? useConversationsStore().byId(c.conversationId)?.mutedUntil : null
    return !(muted && new Date(muted).getTime() > Date.now())
  }

  function ring(c: CallDto): void {
    incomingId.value = c.id
    clearTimeout(ringTimer)
    const left = ringSeconds() * 1000 - (Date.now() - new Date(c.startedAt).getTime())
    // 服务器到点会发 CallUpdated(未接)；这里兜底，防止事件丢了铃一直响
    ringTimer = setTimeout(() => { if (incomingId.value === c.id) clearIncoming() }, Math.max(1000, left + 3000))
    // 已经在别的通话里：只弹窗，不盖过通话的声音
    if (!current.value) startRingtone('incoming')
  }

  function clearIncoming(): void {
    const id = incomingId.value
    if (!id) return
    incomingId.value = null
    clearTimeout(ringTimer)
    closeCallNotifications(id)
    if (current.value?.phase !== 'outgoing') stopRingtone()
  }

  /** 这台设备上还挂着的这通通话的系统通知（带接听 / 拒绝按钮的那条）一并收掉 */
  function closeCallNotifications(callId: string): void {
    if (typeof navigator === 'undefined' || !navigator.serviceWorker) return
    void navigator.serviceWorker.ready
      .then((r) => r.getNotifications({ tag: `call:${callId}` }))
      .then((list) => list.forEach((n) => n.close()))
      .catch(() => undefined)
  }

  // ---------------------------------------------------------------- SignalR 事件

  function onIncomingCall(c: CallDto): void {
    calls.value[c.id] = c
    if (shouldRing(c) && !incomingId.value) ring(c)
  }

  function onCallUpdated(c: CallDto): void {
    const cur = current.value
    if (c.endedAt) {
      if (incomingId.value === c.id) clearIncoming()
      closeCallNotifications(c.id)
      if (cur?.callId === c.id) {
        const outgoing = cur.phase === 'outgoing'
        calls.value[c.id] = c
        finish(outgoing && c.endReason === 'Declined' ? 'calls.declinedByPeer' : outgoing && c.endReason === 'Missed' ? 'calls.noAnswer' : 'calls.ended')
      }
      delete calls.value[c.id]
      dismissed.delete(c.id)
      return
    }
    calls.value[c.id] = c
    // 群聊里别人接了：不再算响铃（和 refresh 的判断一致），弹窗收起，聊天页仍可"加入通话"
    if (incomingId.value === c.id && !isRinging(c)) clearIncoming()
    if (cur?.callId === c.id) {
      if (cur.phase === 'outgoing' && c.answeredAt) {
        cur.phase = 'active'
        stopRingtone()
      }
      syncSubscriptions()
    }
  }

  function onParticipantJoined(e: ParticipantEvent): void {
    const c = calls.value[e.callId]
    if (c) {
      c.participants = [...c.participants.filter((p) => p.id !== e.participant.id), e.participant]
      if (!c.joinedUserIds.includes(e.participant.userId)) c.joinedUserIds = [...c.joinedUserIds, e.participant.userId]
    }
    // 我在另一台设备上接了：这边不用再响
    if (e.participant.userId === me() && incomingId.value === e.callId && current.value?.participantId !== e.participant.id) clearIncoming()
    if (current.value?.callId === e.callId) syncSubscriptions()
  }

  function onParticipantLeft(e: ParticipantLeftEvent): void {
    const c = calls.value[e.callId]
    if (c) c.participants = c.participants.filter((p) => p.id !== e.participantId)
    const cur = current.value
    if (cur?.callId !== e.callId) return
    if (e.participantId === cur.participantId || selfReplaced.has(e.participantId)) {
      // 重连时自己顶掉的旧身份：事件可能比 join 的响应先到，忽略
      if (selfReplaced.delete(e.participantId)) return
      // 被顶掉（别的设备接听了）或会议室被清场：本地收场，不用再通知服务器；超时被清理的，重新加入
      if (e.reason === 'replaced') finish('calls.replaced')
      else if (e.reason === 'ended') finish('calls.ended')
      else void restart()
      return
    }
    dropRemote(e.participantId)
    syncSubscriptions()
  }

  function onParticipantUpdated(e: ParticipantEvent): void {
    const c = calls.value[e.callId]
    if (!c) return
    c.participants = c.participants.map((p) => (p.id === e.participant.id ? { ...e.participant, tracks: p.tracks } : p))
  }

  function onTrackPublished(e: TrackEvent): void {
    const p = calls.value[e.callId]?.participants.find((x) => x.id === e.participantId)
    if (p && !p.tracks.some((t) => t.trackName === e.trackName)) p.tracks = [...p.tracks, { trackName: e.trackName, kind: e.kind }]
    if (current.value?.callId === e.callId) syncSubscriptions()
  }

  function onTrackUnpublished(e: TrackEvent): void {
    const p = calls.value[e.callId]?.participants.find((x) => x.id === e.participantId)
    if (p) p.tracks = p.tracks.filter((t) => t.trackName !== e.trackName)
    if (current.value?.callId === e.callId) syncSubscriptions()
  }

  function onCallDeclined(e: CallDeclinedEvent): void {
    if (e.userId === me()) {
      // 我在另一台设备上拒接了
      dismissed.add(e.callId)
      if (incomingId.value === e.callId) clearIncoming()
    } else if (current.value?.callId === e.callId && current.value.phase === 'outgoing') {
      notify('calls.someoneDeclined', 'info', { userId: e.userId })
    }
  }

  // ---------------------------------------------------------------- 发起 / 接听 / 拒接 / 挂断

  /** 在会话里发起通话（已有进行中的就加入它） */
  async function startCall(conversationId: string, type: CallType): Promise<boolean> {
    if (!ensureUsable() || busy.value) return false
    starting.value = true
    try {
      return await startIn(conversationId, type)
    } finally {
      starting.value = false
    }
  }

  async function startIn(conversationId: string, type: CallType): Promise<boolean> {
    let started: { call: CallDto; created: boolean }
    try {
      started = await callsApi.start(conversationId, type)
    } catch (e) {
      notify(failureKey(e), 'error')
      return false
    }
    const { call, created } = started
    calls.value[call.id] = call
    const ok = await enter(call.id, call.type !== 'Audio')
    // 这次新建的通话自己却没能进去（没有麦克风、加入失败）：取消掉，别让对方空响、接起来是一通没人的电话
    if (!ok && created && current.value?.callId !== call.id) {
      log('发起后自己没能进入，取消这通通话')
      void callsApi.end(call.id, true).catch(() => undefined)
    }
    return ok
  }

  /** 给某个家人打电话：单聊不存在就先建 */
  async function callMember(userId: string, type: CallType): Promise<boolean> {
    if (!ensureUsable() || busy.value) return false
    starting.value = true
    try {
      const { data } = await api.POST('/api/conversations', { body: { type: 'Direct', title: null, memberIds: [userId] } })
      if (!data) throw new Error('no conversation')
      const conversations = useConversationsStore()
      if (!conversations.byId(data.id)) await conversations.load()
      return await startIn(data.id, type)
    } catch {
      notify('calls.startFailed', 'error')
      return false
    } finally {
      starting.value = false
    }
  }

  function accept(): Promise<boolean> {
    const c = incoming.value
    if (!c) return Promise.resolve(false)
    return enter(c.id, c.type !== 'Audio')
  }

  /** 进客厅或加入进行中的通话 */
  function join(callId: string): Promise<boolean> {
    const c = calls.value[callId]
    return enter(callId, c ? c.type !== 'Audio' : true)
  }

  function decline(callId?: string): void {
    const id = callId ?? incomingId.value
    if (!id) return
    dismissed.add(id)
    if (incomingId.value === id) clearIncoming()
    closeCallNotifications(id)
    void callsApi.decline(id).catch(() => undefined)
  }

  /** 从通知点进来（?call=…）：还在响就弹来电，点的是"接听"就直接接 */
  async function openFromLink(callId: string, action?: string): Promise<void> {
    if (current.value?.callId === callId) return
    let c: CallDto
    try {
      c = await callsApi.get(callId)
    } catch {
      return
    }
    if (c.endedAt) {
      notify('calls.alreadyEnded')
      return
    }
    calls.value[c.id] = c
    if (action === 'answer') {
      dismissed.delete(c.id)
      await enter(c.id, c.type !== 'Audio')
    } else if (shouldRing(c) && !incomingId.value) {
      ring(c)
    }
  }

  function ensureUsable(): boolean {
    if (!callsSupported()) {
      notify('calls.unsupported', 'warning')
      return false
    }
    if (config.value && !config.value.enabled) {
      notify('calls.notConfigured', 'warning')
      return false
    }
    return true
  }

  function failureKey(e: unknown): string {
    const status = e instanceof SignalingError ? e.status : -1
    if (status === 410 || status === 404) return 'calls.alreadyEnded'
    if (status === 503) return 'calls.notConfigured'
    if (status === 0) return 'calls.offline'
    return 'calls.startFailed'
  }

  /**
   * 进入一通通话：先拿到麦克风（拿不到就不进，免得对方接起一个哑巴电话），摄像头拿不到则退成只有语音；
   * 然后加入、建连接、发布自己的轨道、订阅已经在里面的人。
   */
  async function enter(callId: string, video: boolean): Promise<boolean> {
    if (!ensureUsable() || entering.value) return false
    if (current.value?.callId === callId) {
      current.value.minimized = false
      return true
    }
    entering.value = true
    // 明确点了接听 / 加入：之前的拒接不算数；之后再点拒接（dismissed 里又有了它）就中止这次进入
    dismissed.delete(callId)
    try {
      if (current.value) await hangup()
      const gen = ++generation
      // 点了接听：铃先停；弹窗留到真正进入为止（麦克风没给权限时还能再点一次或拒接）
      if (incomingId.value === callId) stopRingtone()

      try {
        mic = await getMicrophone()
        watchCapture(mic, 'Audio')
      } catch (e) {
        const why = mediaFailure(e)
        notify(why === 'denied' ? 'calls.micDenied' : why === 'not-found' ? 'calls.micMissing' : 'calls.micFailed', 'error')
        return false
      }
      if (video) {
        try {
          cam = await getCamera(facing)
          watchCapture(cam, 'Video')
        } catch {
          notify('calls.camFailed', 'warning')
        }
      }
      if (gen !== generation || dismissed.has(callId)) {
        releaseMedia()
        return false
      }

      let joined
      try {
        joined = await callsApi.join(callId)
      } catch (e) {
        releaseMedia()
        if (!dismissed.has(callId)) notify(failureKey(e), 'error')
        if (e instanceof SignalingError && (e.status === 410 || e.status === 404)) {
          if (incomingId.value === callId) clearIncoming()
          delete calls.value[callId]
        }
        return false
      }
      if (gen !== generation || dismissed.has(callId)) {
        // 等加入的这段时间里已经收场（挂断 / 通话结束 / 换了别的通话 / 又点了拒接）
        log(`加入期间通话已收场，退出（gen ${gen} → ${generation}）`)
        releaseMedia()
        void callsApi.leave(callId, joined.participantId)
        return false
      }

      calls.value[callId] = joined.call
      dismissed.delete(callId)
      if (incomingId.value === callId) clearIncoming()
      const ringing = !joined.call.roomSlug && !joined.call.answeredAt
      log(`已加入通话 ${callId.slice(0, 8)}，参与者 ${joined.participantId.slice(0, 8)}${ringing ? '，等待接听' : ''}`)
      current.value = {
        callId, participantId: joined.participantId, phase: ringing ? 'outgoing' : 'active',
        micOn: true, camOn: !!cam, screenOn: false, minimized: false, mediaState: 'connecting',
      }
      localVideo.value = cam
      restartRounds = 0
      if (ringing) startRingtone('outgoing')
      startHeartbeat(callId)
      void acquireWakeLock()
      startSession(callId, joined.participantId, joined.iceServers)
      const ok = await publishLocal(callId)
      // 第一次发布就失败（连接问题）：onConnectionLost 没接住的话在这里补一次重连
      if (!ok && gen === generation && !restarting) void restart()
      // 加入的快照可能比这期间经 SignalR 收到的事件旧（别人几乎同时进来）：再对一次账，补上漏掉的人和轨道
      if (ok && gen === generation && !restarting) void reconcileCurrent()
      return true
    } finally {
      entering.value = false
    }
  }

  async function hangup(): Promise<void> {
    const cur = current.value
    if (!cur) return
    const id = cur.callId
    const call = calls.value[id]
    log('挂断')
    finish()
    // 服务器的事件随后也会到；先本地去掉自己，界面立刻对
    if (call) call.participants = call.participants.filter((p) => p.id !== cur.participantId)
    await callsApi.leave(id, cur.participantId)
  }

  /** 本地收场：停铃声、心跳、连接、采集。不通知服务器（调用方决定） */
  function finish(noticeKey?: string): void {
    const cur = current.value
    if (cur) log(`通话收场${noticeKey ? `：${noticeKey}` : ''}`)
    generation++
    restarting = false
    stopRingtone()
    clearInterval(heartbeatTimer)
    heartbeatTimer = undefined
    closeSession()
    releaseMedia()
    remote.value = {}
    void wakeLock?.release().catch(() => undefined)
    wakeLock = null
    current.value = null
    selfReplaced.clear()
    if (cur) {
      const c = calls.value[cur.callId]
      if (c?.endedAt) delete calls.value[cur.callId]
    }
    if (noticeKey) notify(noticeKey)
    // 通话期间又来了别的电话：现在可以响了
    if (incomingId.value && isRinging(incoming.value ?? undefined)) startRingtone('incoming')
  }

  function releaseMedia(): void {
    stopTrack(mic)
    stopTrack(cam)
    stopTrack(screen)
    placeholder?.stop()
    mic = cam = screen = null
    placeholder = null
    localVideo.value = null
    localScreen.value = null
  }

  // ---------------------------------------------------------------- 媒体连接

  function startSession(callId: string, participantId: string, iceServers: IceServerConfig[]): void {
    sessionLost = false
    session = new RealtimeSession(iceServers, callsApi.signaling(callId, participantId), {
      onRemoteTrack: (r, track) => setRemote(r, track),
      onRemoteTrackGone: (r) => setRemote(r, null),
      onConnectionLost: () => {
        sessionLost = true
        void restart()
      },
      onState: (s) => {
        if (current.value) current.value.mediaState = s
        if (s === 'connected') restartRounds = 0
      },
    })
    syncSubscriptions()
  }

  function setRemote(r: RemoteTrackRef, track: MediaStreamTrack | null): void {
    const slot = SLOT[r.kind]
    const entry = { ...(remote.value[r.participantId] ?? {}) }
    if (track) entry[slot] = track
    else delete entry[slot]
    remote.value = { ...remote.value, [r.participantId]: entry }
  }

  function dropRemote(participantId: string): void {
    if (!(participantId in remote.value)) return
    const next = { ...remote.value }
    delete next[participantId]
    remote.value = next
  }

  /** 想收的 = 通话里其他人正在发布的全部轨道；RealtimeSession 自己对账（新增的订阅、没了的关闭） */
  function syncSubscriptions(): void {
    if (!session || !current.value) return
    const refs: RemoteTrackRef[] = []
    for (const p of others.value) for (const t of p.tracks) refs.push({ participantId: p.id, trackName: t.trackName, kind: t.kind })
    session.setSubscriptions(refs)
    // 人走了但没收到单独的事件（快照替换）：把多余的画面清掉
    const alive = new Set(others.value.map((p) => p.id))
    for (const id of Object.keys(remote.value)) if (!alive.has(id)) dropRemote(id)
  }

  /** 把手上的本地轨道发布出去（首次进入、重连后）。返回是否成功 */
  async function publishLocal(callId: string): Promise<boolean> {
    const s = session
    const cur = current.value
    if (!s || !cur) return false
    const items: { kind: TrackKind; track: MediaStreamTrack }[] = []
    if (mic) items.push({ kind: 'Audio', track: mic })
    const video = cam ?? placeholder?.track
    if (video) items.push({ kind: 'Video', track: video })
    if (screen) items.push({ kind: 'Screen', track: screen })
    try {
      await s.publish(items)
    } catch (e) {
      log('发布本地轨道失败', e)
      return false
    }
    if (session !== s) return false
    // 服务器默认"麦克风开、摄像头关"，发布视频时会自动记成开：和实际不一样时才上报
    if (!cur.micOn || (!!video && !cur.camOn)) void pushState(callId)
    return true
  }

  function pushState(callId: string): Promise<void> {
    const cur = current.value
    if (!cur) return Promise.resolve()
    return callsApi.setState(callId, cur.participantId, cur.micOn, cur.camOn).then(() => undefined, () => undefined)
  }

  /**
   * 媒体连接断了（ICE 失败、协商结果不明、被服务器按掉线清理）：重新加入换一对新的 Cloudflare 会话，
   * 本地采集的轨道接着用。服务器会把旧的那份标记为"被顶掉"，别人看到的是我短暂离开又回来。
   */
  async function restart(): Promise<void> {
    const cur = current.value
    if (!cur || restarting) return
    restarting = true
    const gen = generation
    const callId = cur.callId
    const stale = (): boolean => gen !== generation || current.value !== cur
    if (++restartRounds > MAX_RESTART_ROUNDS) {
      log('连续重连多轮仍不稳定，放弃')
      void callsApi.leave(callId, cur.participantId)
      finish('calls.lost')
      return
    }
    const before = cur.phase === 'reconnecting' ? 'active' : cur.phase
    cur.phase = 'reconnecting'
    closeSession()
    remote.value = {}
    log(`媒体连接断开，重新加入（第 ${restartRounds} 轮）`)

    for (const wait of RESTART_DELAYS_MS) {
      if (wait) await delay(wait)
      if (stale()) return
      try {
        // 重新加入会把现在这份身份顶掉：先记下来，收到它的 ParticipantLeft 时不当回事
        selfReplaced.add(cur.participantId)
        // 带上原来的身份：如果它其实是被我别的设备顶掉的，服务器返回 410 replaced，而不是反过来把那台设备顶掉
        const joined = await callsApi.join(callId, cur.participantId)
        if (stale()) {
          log('重新加入期间通话已收场，退出')
          void callsApi.leave(callId, joined.participantId)
          return
        }
        // 重连期间收到的"已接通"比加入快照新：别被快照盖回"响铃中"
        const known = calls.value[callId]
        calls.value[callId] = joined.call.answeredAt || !known?.answeredAt ? joined.call : { ...joined.call, answeredAt: known.answeredAt }
        const latest = calls.value[callId]!
        cur.participantId = joined.participantId
        startSession(callId, joined.participantId, joined.iceServers)
        const ok = await publishLocal(callId)
        if (stale()) return
        if (ok && !sessionLost) {
          cur.phase = !latest.roomSlug && !latest.answeredAt ? 'outgoing' : before === 'outgoing' ? 'active' : before
          if (cur.phase !== 'outgoing') stopRingtone()
          restarting = false
          void reconcileCurrent()
          return
        }
        closeSession()
      } catch (e) {
        if (stale()) return
        log('重新加入失败', e)
        // 我在别的设备上还在这通通话里（这台是被顶掉的）：就地收场
        if (isReplaced(e)) {
          finish('calls.replaced')
          return
        }
        // 通话已经结束 / 被移出会话：不用再试
        if (e instanceof SignalingError && [403, 404, 410].includes(e.status)) {
          finish('calls.ended')
          return
        }
      }
    }
    log('多次重连失败，放弃')
    void callsApi.leave(callId, cur.participantId)
    finish('calls.lost')
  }

  /**
   * 和服务器对一下我这通通话：结束了就收场；我这台设备已经不在里面了——别的设备上的我还在，说明被顶掉了，
   * 就地收场（重新加入会把那台设备反过来顶掉）；否则（断线太久被清理）重新加入。顺带补上漏掉的"已接通"。
   */
  async function reconcileCurrent(): Promise<void> {
    const cur = current.value
    if (!cur || restarting) return
    const pid = cur.participantId
    try {
      const c = await callsApi.get(cur.callId)
      // 等响应期间开始了重连 / 换了身份：这份快照已经不能拿来判断
      if (current.value !== cur || restarting || cur.participantId !== pid) return
      calls.value[c.id] = c
      if (c.endedAt) {
        finish('calls.ended')
        return
      }
      if (!c.participants.some((p) => p.id === pid)) {
        if (c.participants.some((p) => p.userId === me() && p.id !== pid && !selfReplaced.has(p.id))) finish('calls.replaced')
        else void restart()
        return
      }
      if (cur.phase === 'outgoing' && (c.answeredAt || c.roomSlug)) {
        cur.phase = 'active'
        stopRingtone()
      }
      syncSubscriptions()
    } catch (e) {
      if (e instanceof SignalingError && [403, 404].includes(e.status) && current.value === cur) finish('calls.ended')
    }
  }

  function startHeartbeat(callId: string): void {
    clearInterval(heartbeatTimer)
    const every = heartbeatMs()
    lastBeat = Date.now()
    heartbeatTimer = setInterval(() => {
      // 定时器迟到了很久、而页面在后台：页面被冻结过（采集多半也停了）
      const now = Date.now()
      if (now - lastBeat > every + 15_000 && (hiddenAt || document.visibilityState === 'hidden')) stalledWhileHidden = true
      lastBeat = now
      const cur = current.value
      // 重连期间身份在换：旧身份的心跳必然 410，别据此收场
      if (!cur || cur.callId !== callId || restarting) return
      const pid = cur.participantId
      callsApi.heartbeat(callId, pid).catch((e: unknown) => {
        if (current.value !== cur || cur.participantId !== pid || restarting) return
        // 410 replaced：我在别的设备上了，这边收场；其它 410：断网太久被清理，对账后重新加入。别的错误（断网）先不管
        if (isReplaced(e)) finish('calls.replaced')
        else if (e instanceof SignalingError && e.status === 410) void reconcileCurrent()
      })
    }, every)
  }

  function heartbeatMs(): number {
    return (config.value?.heartbeatSeconds ?? 10) * 1000
  }

  /**
   * 盯住采集轨道：设备被拔掉 / 换走（AirPods 收进盒子、USB 耳机或摄像头拔了）时轨道会 ended，
   * 不处理的话对方听到一片静默、看到定格画面，界面却显示麦克风开着；后台期间被系统暂停也记一笔。
   */
  function watchCapture(track: MediaStreamTrack, kind: 'Audio' | 'Video'): void {
    track.addEventListener('ended', () => {
      if (kind === 'Audio' ? mic === track : cam === track) void (kind === 'Audio' ? recoverMic(track) : recoverCam(track))
    })
    track.addEventListener('mute', () => {
      if (document.visibilityState === 'hidden') mutedWhileHidden = true
    })
  }

  /** 麦克风没了：换一个（系统通常切到内置麦克风），拿不到就标记为静音并提示 */
  async function recoverMic(dead: MediaStreamTrack): Promise<void> {
    const cur = current.value
    if (!cur) return
    log('麦克风断开，重新获取')
    let next: MediaStreamTrack
    try {
      next = await getMicrophone()
    } catch {
      if (current.value === cur && mic === dead) {
        cur.micOn = false
        void pushState(cur.callId)
        notify('calls.micFailed', 'error')
      }
      return
    }
    if (current.value !== cur || mic !== dead) {
      stopTrack(next)
      return
    }
    next.enabled = cur.micOn
    mic = next
    watchCapture(next, 'Audio')
    await session?.replaceTrack('Audio', next).catch(() => undefined)
  }

  /** 摄像头没了：换成黑画面占位（发布的视频轨道一直有包，Cloudflare 不回收），标记为关闭 */
  async function recoverCam(dead: MediaStreamTrack): Promise<void> {
    const cur = current.value
    if (!cur || cam !== dead) return
    log('摄像头断开，换成占位画面')
    cam = null
    localVideo.value = null
    cur.camOn = false
    placeholder ??= placeholderVideo()
    void pushState(cur.callId)
    notify('calls.camFailed', 'warning')
    await session?.replaceTrack('Video', placeholder.track).catch(() => undefined)
  }

  /** 通话中不让屏幕熄灭（手机上熄屏会掐掉采集） */
  async function acquireWakeLock(): Promise<void> {
    try {
      const nav = navigator as Navigator & { wakeLock?: { request(type: 'screen'): Promise<{ release(): Promise<void> }> } }
      if (nav.wakeLock && document.visibilityState === 'visible') wakeLock = await nav.wakeLock.request('screen')
    } catch {
      // 不支持或被拒绝：无妨
    }
  }

  // ---------------------------------------------------------------- 通话中的开关

  function toggleMic(): void {
    const cur = current.value
    if (!cur || !mic) return
    if (!cur.micOn && mic.readyState === 'ended') {
      // 之前麦克风被拔掉、换新的也没成功：现在再拿一次。发布的音频轨道很久没有包，可能已被 Cloudflare 回收，换好后重新发布
      const dead = mic
      cur.micOn = true
      void recoverMic(dead).then(() => {
        if (current.value === cur && mic !== dead) void restart()
      })
      return
    }
    cur.micOn = !cur.micOn
    // 静音只是不出声：轨道仍在发静音帧，Cloudflare 不会回收它
    mic.enabled = cur.micOn
    void pushState(cur.callId)
  }

  async function toggleCam(): Promise<void> {
    const cur = current.value
    if (!cur || !session || cur.phase === 'reconnecting' || camBusy.value) return
    camBusy.value = true
    try {
      await toggleCamCore(cur, session)
    } finally {
      camBusy.value = false
    }
  }

  async function toggleCamCore(cur: CurrentCall, s: RealtimeSession): Promise<void> {
    if (cur.camOn) {
      // 真的关掉摄像头（指示灯熄灭），发布的那条视频轨道换成黑画面占位。
      // 先把摄像头从状态里拿掉再等换源：这期间开始的重连会发布占位画面，而不是一个马上要停掉的摄像头
      const old = cam
      cam = null
      localVideo.value = null
      cur.camOn = false
      placeholder ??= placeholderVideo()
      await s.replaceTrack('Video', placeholder.track).catch(() => undefined)
      stopTrack(old)
      void pushState(cur.callId)
      return
    }
    let track: MediaStreamTrack
    try {
      track = await getCamera(facing)
    } catch (e) {
      notify(mediaFailure(e) === 'denied' ? 'calls.camDenied' : 'calls.camFailed', 'warning')
      return
    }
    if (current.value !== cur || session !== s) {
      stopTrack(track)
      return
    }
    stopTrack(cam)
    cam = track
    watchCapture(track, 'Video')
    localVideo.value = track
    cur.camOn = true
    try {
      // 语音通话里第一次开摄像头：新发布一条视频轨道；之后都是换源
      if (s.isPublished('Video')) await s.replaceTrack('Video', track)
      else await s.publish([{ kind: 'Video', track }])
    } catch {
      // 协商失败会触发重连，重连后带着摄像头重新发布
    }
    placeholder?.stop()
    placeholder = null
    void pushState(cur.callId)
  }

  /** 前后摄像头切换（手机） */
  async function flipCamera(): Promise<void> {
    const cur = current.value
    if (!cur || !session || !cur.camOn || cur.phase === 'reconnecting' || camBusy.value) return
    camBusy.value = true
    try {
      const next: Facing = facing === 'user' ? 'environment' : 'user'
      // 不少手机不能同时打开两个摄像头：先停旧的
      stopTrack(cam)
      cam = null
      let track: MediaStreamTrack | null = null
      try {
        track = await getCamera(next)
        facing = next
      } catch {
        try { track = await getCamera(facing) } catch { track = null }
      }
      if (current.value !== cur) {
        stopTrack(track)
        return
      }
      cam = track
      if (track) watchCapture(track, 'Video')
      localVideo.value = track
      if (track) {
        const s2 = session
        // 切换期间完成了一次重连、新会话里还没有视频发布：发布它；否则换源
        if (s2 && !s2.isPublished('Video')) await s2.publish([{ kind: 'Video', track }]).catch(() => undefined)
        else await s2?.replaceTrack('Video', track).catch(() => undefined)
      }
      else {
        // 两个摄像头都打不开：换成黑画面占位，标记为关闭
        cur.camOn = false
        placeholder ??= placeholderVideo()
        await session?.replaceTrack('Video', placeholder.track).catch(() => undefined)
        void pushState(cur.callId)
      }
    } finally {
      camBusy.value = false
    }
  }

  async function toggleScreen(): Promise<void> {
    const cur = current.value
    if (!cur || !session || cur.phase === 'reconnecting') return
    if (cur.screenOn) {
      await stopScreen()
      return
    }
    let track: MediaStreamTrack
    try {
      track = await getScreen()
    } catch (e) {
      // 用户在选择窗口里点了取消也是 NotAllowedError：不提示
      if (mediaFailure(e) !== 'denied') notify('calls.screenFailed', 'warning')
      return
    }
    if (current.value !== cur || !session) {
      stopTrack(track)
      return
    }
    screen = track
    localScreen.value = track
    cur.screenOn = true
    // 用户点浏览器自带的"停止共享"
    track.addEventListener('ended', () => { if (screen === track) void stopScreen() })
    try {
      await session.publish([{ kind: 'Screen', track }])
    } catch {
      // 被服务器拒绝（已回滚）：撤回本地状态；连接问题则由重连接手，重连后会带着屏幕重新发布
      if (session && !sessionLost && screen === track) {
        stopTrack(track)
        screen = null
        localScreen.value = null
        cur.screenOn = false
        notify('calls.screenFailed', 'warning')
      }
    }
  }

  async function stopScreen(): Promise<void> {
    const cur = current.value
    const track = screen
    screen = null
    localScreen.value = null
    if (cur) cur.screenOn = false
    stopTrack(track)
    await session?.unpublish('Screen').catch(() => undefined)
  }

  function setMinimized(v: boolean): void {
    if (current.value) current.value.minimized = v
  }

  // 关页面 / 刷新：告诉服务器我走了，不等 35 秒的心跳超时。
  // 手机上只有 pagehide 可靠；桌面 Chrome 导航离开时 pagehide 里发的 beacon 偶尔来不及送出，beforeunload 更早一步。两个都挂，只发一次。
  // beforeunload 可能被取消（网盘还有没传完的文件时会弹"确定离开？"）：那种情况只靠 pagehide，免得点了"留下"通话却已经挂了。
  if (typeof window !== 'undefined') {
    let leftByUnload: string | null = null
    const leaveOnUnload = (event: Event): void => {
      const cur = current.value
      if (!cur || leftByUnload === cur.participantId) return
      if (event.type === 'beforeunload' && useDriveUploadsStore().unfinished > 0) return
      leftByUnload = cur.participantId
      log(`页面关闭（${event.type}），离开通话`)
      callsApi.leaveBeacon(cur.callId, cur.participantId)
    }
    window.addEventListener('beforeunload', leaveOnUnload)
    window.addEventListener('pagehide', leaveOnUnload)
    // 从往返缓存里恢复的页面没有真的离开过：之后再关时还要能发
    window.addEventListener('pageshow', (e) => { if ((e as PageTransitionEvent).persisted) leftByUnload = null })
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') {
        hiddenAt = Date.now()
        mutedWhileHidden = false
        stalledWhileHidden = false
        return
      }
      const cur = current.value
      const away = hiddenAt ? Date.now() - hiddenAt : 0
      hiddenAt = 0
      if (!cur) return
      // 切回前台：唤醒锁在页面隐藏时会被系统收回
      void acquireWakeLock()
      // 在后台待太久、而且确实被暂停过（采集被系统静音、页面被冻结——占位画面也跟着停了）：发布的轨道多半已被
      // Cloudflare 回收，重新加入、重新发布。桌面切到别的标签页时采集和心跳都照常，只和服务器对一下
      const suspended = mutedWhileHidden || stalledWhileHidden || !!cam?.muted || !!mic?.muted
        || (lastBeat > 0 && Date.now() - lastBeat > Math.max(BACKGROUND_REPUBLISH_MS, heartbeatMs() + 15_000))
      stalledWhileHidden = false
      if (away >= BACKGROUND_REPUBLISH_MS && !restarting && suspended) {
        log(`在后台 ${Math.round(away / 1000)} 秒，重新发布`)
        void restart()
      } else {
        void reconcileCurrent()
      }
    })
  }

  return {
    config, enabled, calls, incoming, current, currentCall, others, rooms, ongoing, remote, localVideo, localScreen, notice, entering, busy, camBusy,
    init, refresh, ongoingIn,
    onIncomingCall, onCallUpdated, onParticipantJoined, onParticipantLeft, onParticipantUpdated, onTrackPublished, onTrackUnpublished, onCallDeclined,
    startCall, callMember, accept, join, decline, openFromLink, hangup,
    toggleMic, toggleCam, flipCamera, toggleScreen, setMinimized,
  }
})
