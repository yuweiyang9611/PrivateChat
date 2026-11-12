export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 ** 2) return `${Math.round(n / 1024)} KB`
  if (n < 1024 ** 3) return `${(n / 1024 ** 2).toFixed(1)} MB`
  return `${(n / 1024 ** 3).toFixed(2)} GB`
}

export function formatKbps(kbps: number): string {
  return kbps >= 1000 ? `${(kbps / 1000).toFixed(1)} Mbps` : `${kbps} kbps`
}

const pad = (n: number) => String(n).padStart(2, '0')

/** 今天 14:02 / 昨天 14:02 / 3月5日 14:02 / 2025年3月5日 */
export function formatWhen(iso: string, now = new Date()): string {
  const d = new Date(iso)
  const time = `${pad(d.getHours())}:${pad(d.getMinutes())}`
  const day = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime()
  const diffDays = Math.round((day(now) - day(d)) / 86_400_000)
  if (diffDays === 0) return time
  if (diffDays === 1) return `昨天 ${time}`
  if (d.getFullYear() === now.getFullYear()) return `${d.getMonth() + 1}月${d.getDate()}日 ${time}`
  return `${d.getFullYear()}年${d.getMonth() + 1}月${d.getDate()}日`
}

/** 会话持续了几分钟（从同意算起）；没开始过返回 null */
export function sessionMinutes(s: { acceptedAt: string | null; endedAt: string | null }, now = Date.now()): number | null {
  if (!s.acceptedAt) return null
  const end = s.endedAt ? new Date(s.endedAt).getTime() : now
  return Math.max(0, Math.floor((end - new Date(s.acceptedAt).getTime()) / 60_000))
}
