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
    const problem = r.error as { title?: string; detail?: string } | undefined
    throw new SignalingError(r.response.status, problem?.title ?? `HTTP ${r.response.status}`)
  }
  return r.data as T
}

const at = (id: string) => ({ params: { path: { id } } })

export const callsApi = {
  config: () => unwrap(api.GET('/api/calls/config')),
  active: () => unwrap(api.GET('/api/calls/active')),
  rooms: () => unwrap(api.GET('/api/calls/rooms')),
  history: (conversationId?: string) => unwrap(api.GET('/api/calls', { params: { query: { conversationId, limit: 50 } } })),
  usage: () => unwrap(api.GET('/api/calls/usage')),
  get: (id: string) => unwrap(api.GET('/api/calls/{id}', at(id))),
  start: (conversationId: string, type: CallType) => unwrap(api.POST('/api/calls', { body: { conversationId, type } })),
  join: (id: string) => unwrap(api.POST('/api/calls/{id}/join', at(id))),
  decline: (id: string) => unwrap(api.POST('/api/calls/{id}/decline', at(id))),
  end: (id: string) => unwrap(api.POST('/api/calls/{id}/end', at(id))),
  heartbeat: (id: string) => unwrap(api.POST('/api/calls/{id}/heartbeat', at(id))),
  setState: (id: string, micOn: boolean, camOn: boolean) => unwrap(api.POST('/api/calls/{id}/state', { ...at(id), body: { micOn, camOn } })),

  /** 离开：keepalive 让请求在页面关闭 / 跳转后也能发完 */
  async leave(id: string): Promise<void> {
    try {
      await fetch(`/api/calls/${id}/leave`, { method: 'POST', credentials: 'include', keepalive: true })
    } catch {
      // 发不出去也没关系：心跳停了，服务器 35 秒后按掉线处理
    }
  },

  /** 关页面时用：sendBeacon 不等响应，浏览器保证尽量送达 */
  leaveBeacon(id: string): void {
    try {
      // 带个标记，服务器日志里能看出这是关页面触发的
      if (!navigator.sendBeacon?.(`/api/calls/${id}/leave?via=unload`)) {
        console.warn('[calls] sendBeacon 失败，改用 fetch keepalive')
        void this.leave(id)
      }
    } catch {
      // ignore
    }
  },

  signaling(id: string): Signaling {
    return {
      publish: (offer, tracks) => unwrap(api.POST('/api/calls/{id}/publish', { ...at(id), body: { offer, tracks } })),
      unpublish: (trackNames, offer) => unwrap(api.POST('/api/calls/{id}/unpublish', { ...at(id), body: { trackNames, offer: offer ?? null } })),
      pull: (tracks) => unwrap(api.POST('/api/calls/{id}/pull', { ...at(id), body: { tracks } })),
      renegotiate: async (answer) => { await unwrap(api.POST('/api/calls/{id}/renegotiate', { ...at(id), body: { answer } })) },
      closePulled: async (mids) => { await unwrap(api.POST('/api/calls/{id}/pull/close', { ...at(id), body: { mids } })) },
    }
  },
}
