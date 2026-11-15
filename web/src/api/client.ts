import createClient, { type Middleware } from 'openapi-fetch'
import type { components, paths } from './schema'
import { isOnline } from '@/net/online'

export type UserDto = components['schemas']['UserDto']
export type ConversationDto = components['schemas']['ConversationDto']
export type MessageDto = components['schemas']['MessageDto']

const REAUTH_KEY = 'pc.reauthAt'
const REAUTH_COOLDOWN_MS = 60_000

/**
 * Cloudflare Access 会话过期：整页导航到 /api/auth/reauth。
 * 不能用 location.reload()——Service Worker 会从缓存直接返回页面，请求到不了 Cloudflare 边缘，
 * Access 没机会跳登录页，只会无限刷新。/api 路径不经 Service Worker，Access 拦下后去登录，登录完回到原页面。
 * 60 秒内只跳一次，防止配置异常时来回跳转。返回是否真的发起了跳转。
 */
export function reauthenticate(): boolean {
  try {
    const last = Number(sessionStorage.getItem(REAUTH_KEY) ?? 0)
    if (Date.now() - last < REAUTH_COOLDOWN_MS) return false
    sessionStorage.setItem(REAUTH_KEY, String(Date.now()))
  } catch {
    // 隐私模式下 sessionStorage 不可用，照样跳
  }
  const back = window.location.pathname + window.location.search + window.location.hash
  window.location.assign(`/api/auth/reauth?returnUrl=${encodeURIComponent(back)}`)
  return true
}

/** 401 时的处理（由 session store 注册：向服务器问清原因，再决定重新验证、显示"不是家庭成员"还是回到密码登录页） */
let unauthorizedHandler: (() => void) | null = null
export function setUnauthorizedHandler(h: (() => void) | null): void {
  unauthorizedHandler = h
}

/** 探测判定登录态已丢失时统一处理 */
export function handleAuthLoss(kind: 'unauthorized' | 'access-expired'): void {
  if (kind === 'access-expired') {
    reauthenticate()
    return
  }
  unauthorizedHandler?.()
}

const authGuard: Middleware = {
  async onResponse({ request, response }) {
    // redirect: 'manual' 下，Access 把请求 302 到登录页时这里拿到的是 opaqueredirect
    if (response.type === 'opaqueredirect') handleAuthLoss('access-expired')
    // 密码登录接口的 401 是"密码错误"，不是会话丢失
    else if (response.status === 401 && !new URL(request.url).pathname.startsWith('/api/auth/local/')) handleAuthLoss('unauthorized')
    return response
  },
}

/**
 * 同源客户端：浏览器自动带 Access Cookie 或本地登录 Cookie，前端不管理任何 token。
 * 网络不通时 fetch 会抛 TypeError，调用方需要 try/catch（离线时沿用本地缓存）。
 */
export const api = createClient<paths>({
  baseUrl: '/',
  credentials: 'include',
  // 本站 API 从不重定向；出现重定向只可能是 Access 拦截，不跟随，交给 authGuard
  redirect: 'manual',
})

api.use(authGuard)

export type AuthProbe = 'ok' | 'unauthorized' | 'not-member' | 'access-invalid' | 'access-expired' | 'offline' | 'unreachable'

/** 经 Cloudflare Access 进来的 401 正文里的 code（后端 AccessProblemCodes）；局域网 / 本地模式的 401 没有正文 */
async function accessRejection(res: Response): Promise<{ probe: AuthProbe; email?: string }> {
  try {
    const body = (await res.json()) as { code?: unknown; email?: unknown }
    if (body.code === 'access_not_member') return { probe: 'not-member', email: typeof body.email === 'string' ? body.email : undefined }
    if (body.code === 'access_invalid') return { probe: 'access-invalid' }
  } catch {
    // 没有正文：局域网 / 本地模式没登录
  }
  return { probe: 'unauthorized' }
}

/**
 * 查询当前身份并区分失败原因：
 * - offline：浏览器离线；unreachable：联网但服务器 / 隧道不通（或返回 5xx）；
 * - access-expired：被 Access 重定向；unauthorized：服务器明确 401（局域网 / 本地模式没登录）；
 * - not-member：过了 Access，但这个邮箱不是家庭成员、没被邀请或已停用（带 email）；
 * - access-invalid：带了 Access 身份，但服务器校验不过（多半是配置问题）。
 * 不走 api 客户端，不触发 authGuard，由调用方决定怎么处理。
 */
export async function fetchMe(): Promise<{ probe: AuthProbe; user?: UserDto; email?: string }> {
  if (!isOnline()) return { probe: 'offline' }
  let res: Response
  try {
    res = await fetch('/api/auth/me', {
      credentials: 'include',
      redirect: 'manual',
      cache: 'no-store',
      headers: { Accept: 'application/json' },
    })
  } catch {
    return { probe: isOnline() ? 'unreachable' : 'offline' }
  }
  if (res.type === 'opaqueredirect') return { probe: 'access-expired' }
  if (res.status === 401) return accessRejection(res)
  if (!res.ok) return { probe: 'unreachable' }
  try {
    return { probe: 'ok', user: (await res.json()) as UserDto }
  } catch {
    // 200 但不是 JSON：多半是代理 / 门户页，按不可达处理
    return { probe: 'unreachable' }
  }
}
