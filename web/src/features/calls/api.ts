import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { SignalingError, type Signaling } from './rtc/RealtimeSession'

export type CallDto = components['schemas']['CallDto']
export type CallType = components['schemas']['CallType']
export type CallEndReason = components['schemas']['CallEndReason']
export type ParticipantDto = components['schemas']['ParticipantDto']
export type JoinResponse = components['schemas']['JoinResponse']
export type CallConfigDto = components['schemas']['CallConfigDto']
export type CallUsageDto = components['schemas']['CallUsageDto']

type Result<T> = { data?: T; error?: unknown; response: Response }

/** 统一成 SignalingError：status 0 = 网络不通；其余是服务器的状态码，message 取 ProblemDetails 的说明 */
async function unwrap<T>(request: Promise<Result<T>>): Promise<T> {
  let r: Result<T>
  try {
    r = await request
  } catch {
    throw new SignalingError(0, 'network')
  }
  if (!r.response.ok) {
    const problem = r.error as { title?: string; detail?: string | null } | undefined
    throw new SignalingError(r.response.status, problem?.title ?? `HTTP ${r.response.status}`, problem?.detail ?? undefined)
  }
  return r.data as T
}

const at = (id: string) => ({ params: { path: { id } } })
/** 按设备的接口都带上自己的参与者 Id：同一个人换了设备后，旧设备迟到的请求不会落到新设备上 */
const as = (id: string, participantId: string) => ({ params: { path: { id }, query: { participantId } } })
function leaveUrl(id: string, participantId?: string, via?: string): string {
  const q = new URLSearchParams()
  if (participantId) q.set('participantId', participantId)
  if (via) q.set('via', via)
  const qs = q.toString()
  return `/api/calls/${id}/leave${qs ? '?' + qs : ''}`
}

/** 服务器 410 的 detail：这个人在别的设备上还在通话里（这台设备应就地收场，别重新加入把那边顶掉） */
export const REPLACED = 'replaced'
export function isReplaced(e: unknown): boolean {
  return e instanceof SignalingError && e.status === 410 && e.detail === REPLACED
}

export const callsApi = {
  config: () => unwrap(api.GET('/api/calls/config')),
  active: () => unwrap(api.GET('/api/calls/active')),
  rooms: () => unwrap(api.GET('/api/calls/rooms')),
  history: (conversationId?: string) => unwrap(api.GET('/api/calls', { params: { query: { conversationId, limit: 50 } } })),
  usage: () => unwrap(api.GET('/api/calls/usage')),
  get: (id: string) => unwrap(api.GET('/api/calls/{id}', at(id))),
  /** created：这次请求新建了通话（其他人开始响铃）；false 表示会话里已经有一通进行中的，加入它即可 */
  async start(conversationId: string, type: CallType): Promise<{ call: CallDto; created: boolean }> {
    let created = false
    const call = await unwrap(api.POST('/api/calls', { body: { conversationId, type } }).then((r) => {
      created = r.response.status === 201
      return r
    }))
    return { call, created }
  },
  /** replacing：重连时原来的参与者 Id（已被别的设备顶掉时服务器返回 410 replaced） */
  join: (id: string, replacing?: string) => unwrap(api.POST('/api/calls/{id}/join', { params: { path: { id }, query: { replacing } } })),
  decline: (id: string) => unwrap(api.POST('/api/calls/{id}/decline', at(id))),
  /** ifUnanswered：只在还没人接通时才结束（发起人自己没能进去时取消用；群聊里别人已经接起来聊上了就不动） */
  end: (id: string, ifUnanswered = false) =>
    unwrap(api.POST('/api/calls/{id}/end', { params: { path: { id }, query: ifUnanswered ? { ifUnanswered: true } : {} } })),
  heartbeat: (id: string, participantId: string) => unwrap(api.POST('/api/calls/{id}/heartbeat', as(id, participantId))),
  setState: (id: string, participantId: string, micOn: boolean, camOn: boolean) =>
    unwrap(api.POST('/api/calls/{id}/state', { ...as(id, participantId), body: { micOn, camOn } })),

  /** 离开：keepalive 让请求在页面关闭 / 跳转后也能发完 */
  async leave(id: string, participantId?: string): Promise<void> {
    try {
      await fetch(leaveUrl(id, participantId), { method: 'POST', credentials: 'include', keepalive: true })
    } catch {
      // 发不出去也没关系：心跳停了，服务器 35 秒后按掉线处理
    }
  },

  /** 关页面时用：sendBeacon 不等响应，浏览器保证尽量送达 */
  leaveBeacon(id: string, participantId?: string): void {
    try {
      // 带个标记，服务器日志里能看出这是关页面触发的
      if (!navigator.sendBeacon?.(leaveUrl(id, participantId, 'unload'))) {
        console.warn('[calls] sendBeacon 失败，改用 fetch keepalive')
        void this.leave(id, participantId)
      }
    } catch {
      // ignore
    }
  },

  /** 一个 RealtimeSession 绑定一个参与者身份：重连换了身份就换一套信令 */
  signaling(id: string, participantId: string): Signaling {
    const q = as(id, participantId)
    return {
      publish: (offer, tracks) => unwrap(api.POST('/api/calls/{id}/publish', { ...q, body: { offer, tracks } })),
      unpublish: (trackNames, offer) => unwrap(api.POST('/api/calls/{id}/unpublish', { ...q, body: { trackNames, offer: offer ?? null } })),
      pull: (tracks) => unwrap(api.POST('/api/calls/{id}/pull', { ...q, body: { tracks } })),
      renegotiate: async (answer) => { await unwrap(api.POST('/api/calls/{id}/renegotiate', { ...q, body: { answer } })) },
      closePulled: async (mids) => { await unwrap(api.POST('/api/calls/{id}/pull/close', { ...q, body: { mids } })) },
    }
  },
}
