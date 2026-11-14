/**
 * 搜索的文字处理：分词、高亮、摘要。规则与服务端 SearchService 一致（空白分词、不区分大小写、每个词都要出现）。
 */

export const MAX_QUERY = 100
export const MAX_TERMS = 5

/** 按空白（含全角空格）分词，去重，最多 5 个词 */
export function termsOf(q: string): string[] {
  const seen = new Set<string>()
  const out: string[] = []
  for (const raw of q.trim().slice(0, MAX_QUERY).split(/\s+/u)) {
    // 不做 Unicode 规范化：库里存的是原样（macOS 上的文件名常是 NFD），和服务端一致
    const t = raw
    if (!t || seen.has(t.toLowerCase())) continue
    seen.add(t.toLowerCase())
    out.push(t)
    if (out.length === MAX_TERMS) break
  }
  return out
}

export interface Segment {
  text: string
  hit: boolean
}

/** 把文字切成普通段和命中段（不区分大小写），用于渲染 <mark>。不用 v-html，原文里的尖括号原样显示。 */
export function highlight(text: string, terms: readonly string[]): Segment[] {
  const words = terms.filter(Boolean).map((t) => t.toLowerCase()).sort((a, b) => b.length - a.length)
  if (!text || !words.length) return text ? [{ text, hit: false }] : []
  const lower = text.toLowerCase()
  // toLowerCase 可能改变长度（极少数字符），这时不高亮，避免位置错位
  if (lower.length !== text.length) return [{ text, hit: false }]
  const out: Segment[] = []
  let i = 0
  let plain = ''
  while (i < text.length) {
    const w = words.find((x) => lower.startsWith(x, i))
    if (w) {
      if (plain) out.push({ text: plain, hit: false })
      plain = ''
      out.push({ text: text.slice(i, i + w.length), hit: true })
      i += w.length
    } else {
      plain += text[i]
      i++
    }
  }
  if (plain) out.push({ text: plain, hit: false })
  return out
}

/** 每个词都出现在文字里（不区分大小写） */
export function matchesAll(text: string | null | undefined, terms: readonly string[]): boolean {
  if (!text || !terms.length) return false
  const lower = text.toLowerCase()
  return terms.every((t) => lower.includes(t.toLowerCase()))
}

/** 命中附近的一段（前后约 30 个字，超出用省略号），与服务端摘要一样 */
export function snippet(text: string, terms: readonly string[], radius = 30): string {
  const flat = text.split(/\s+/u).filter(Boolean).join(' ')
  if (flat.length <= radius * 3) return flat
  const lower = flat.toLowerCase()
  let at = -1
  let length = 0
  for (const t of terms) {
    const i = lower.indexOf(t.toLowerCase())
    if (i >= 0 && (at < 0 || i < at)) {
      at = i
      length = t.length
    }
  }
  if (at < 0) at = 0
  let start = Math.max(0, at - radius)
  let end = Math.min(flat.length, at + length + radius * 2)
  if (start > 0 && isLow(flat.charCodeAt(start))) start--
  if (end < flat.length && isLow(flat.charCodeAt(end))) end++
  return (start > 0 ? '…' : '') + flat.slice(start, end) + (end < flat.length ? '…' : '')
}

function isLow(code: number): boolean {
  return code >= 0xdc00 && code <= 0xdfff
}
