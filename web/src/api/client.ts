import createClient, { type Middleware } from 'openapi-fetch'
import type { components, paths } from './schema'

export type UserDto = components['schemas']['UserDto']
export type ConversationDto = components['schemas']['ConversationDto']
export type MessageDto = components['schemas']['MessageDto']

/** Cloudflare Access 会话过期时，请求会被 302 到 <team>.cloudflareaccess.com，整页刷新让用户重新验证 */
export function reloadForAccessLogin(): void {
  window.location.reload()
}

/** 401 时的处理（由 session store 注册：局域网 / 本地模式跳到密码登录页） */
let unauthorizedHandler: (() => void) | null = null
export function setUnauthorizedHandler(h: (() => void) | null): void {
  unauthorizedHandler = h
}

const accessGuard: Middleware = {
  async onResponse({ response }) {
    if (response.redirected && response.url.includes('cloudflareaccess.com')) {
      reloadForAccessLogin()
    } else if (response.status === 401) {
      unauthorizedHandler?.()
    }
    return response
  },
}

/** 同源客户端：浏览器自动带 Access Cookie 或本地登录 Cookie，前端不管理任何 token */
export const api = createClient<paths>({
  baseUrl: '/',
  credentials: 'include',
})

api.use(accessGuard)
