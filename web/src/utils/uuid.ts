/**
 * UUID v4。crypto.randomUUID 只在安全上下文（HTTPS / localhost）里有，
 * 局域网直连 http://服务器IP:8080 时没有它，退回 getRandomValues 自己拼（后者在非安全上下文也可用）。
 * 输出小写、带连字符，和服务端 Guid 的序列化一致。
 */
export function newUuid(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    try {
      return crypto.randomUUID()
    } catch {
      // 某些浏览器在非安全上下文里有这个函数但会抛错
    }
  }
  const b = crypto.getRandomValues(new Uint8Array(16))
  b[6] = (b[6]! & 0x0f) | 0x40
  b[8] = (b[8]! & 0x3f) | 0x80
  const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('')
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`
}
