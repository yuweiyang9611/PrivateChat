import { api } from '@/api/client'
import { unwrap } from '@/stores/remote'
import type { DeviceTelemetryDto, ViewerSessionTelemetry } from './telemetry'

type Result<T> = { data?: T; error?: unknown; response: Response }
type Params = { params: { path: Record<string, string>; query?: Record<string, number> } }

/**
 * 生成的接口类型（schema.d.ts）里还没有遥测的接口（要等服务器跑起来再 gen:api）：返回的类型照 RemoteTelemetry.cs 手写（telemetry.ts），
 * 请求照样经 api 客户端（401、Access 过期照常处理）
 */
const get = api.GET as unknown as <T>(path: string, init: Params) => Promise<Result<T>>

/** 服务器明确不收（老版本没有这个接口、这次远程不是这个人发起的、过了收的时间、数据不对）：这次远程别再报了 */
const REJECTED = new Set([400, 403, 404, 405, 409, 410, 413, 415])

export const telemetryApi = {
  /** 管理员看（设备页的"诊断"）：这台电脑的硬件和能力，和最近 limit 次（1–100）有遥测的远程，新的在前 */
  device: (deviceId: string, limit = 20) =>
    unwrap(get<DeviceTelemetryDto>('/api/telemetry/devices/{deviceId}', { params: { path: { deviceId }, query: { limit } } })),

  /**
   * 控制端这次远程的汇总（ViewerSessionTelemetry，服务器按会话覆盖）。beacon：最后那份（结束、离开）和关页面、刷新时那份用 sendBeacon，
   * 页面关了也送得出去；浏览器不让 sendBeacon 发 JSON 时退回 fetch keepalive。中途那几份也带 keepalive：发到一半跳走了照样发完，
   * 服务器那边不会记成一次中途放弃的请求。
   * 返回 false：服务器明确不收，这次远程别再报了。网络不通、服务器出错返回 true（下次再报）。
   * 不弹提示、不跳去重新登录：遥测出了问题不该打扰用户。
   */
  async viewer(sessionId: string, body: ViewerSessionTelemetry, beacon: boolean): Promise<boolean> {
    const url = `/api/remote/sessions/${encodeURIComponent(sessionId)}/viewer-telemetry`
    const json = JSON.stringify(body)
    if (beacon) {
      try {
        if (navigator.sendBeacon?.(url, new Blob([json], { type: 'application/json' }))) return true
      } catch {
        // 有的浏览器不让 sendBeacon 发 application/json：改用 fetch keepalive
      }
    }
    try {
      const r = await fetch(url, { method: 'POST', credentials: 'include', redirect: 'manual', keepalive: true, headers: { 'Content-Type': 'application/json' }, body: json })
      return !REJECTED.has(r.status)
    } catch {
      return true
    }
  },
}
