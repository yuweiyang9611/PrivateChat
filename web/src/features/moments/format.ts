/** 动态里的时间：刚刚 / N 分钟前 / N 小时前 / 昨天 HH:mm / M月D日 HH:mm / YYYY年M月D日 */
export function relativeTime(iso: string, now: Date = new Date()): string {
  const t = new Date(iso)
  const diff = (now.getTime() - t.getTime()) / 1000
  const hm = `${String(t.getHours()).padStart(2, '0')}:${String(t.getMinutes()).padStart(2, '0')}`
  if (diff < 60) return '刚刚'
  if (diff < 3600) return `${Math.floor(diff / 60)} 分钟前`
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()
  if (t.getTime() >= startOfToday) return `${Math.floor(diff / 3600)} 小时前`
  if (t.getTime() >= startOfToday - 86_400_000) return `昨天 ${hm}`
  if (t.getFullYear() === now.getFullYear()) return `${t.getMonth() + 1}月${t.getDate()}日 ${hm}`
  return `${t.getFullYear()}年${t.getMonth() + 1}月${t.getDate()}日`
}

export interface ReactionLike { userId: string; emoji: string }

/** 按表情分组，保持第一次出现的顺序：[{ emoji: '❤️', userIds: [...] }] */
export function groupReactions(reactions: readonly ReactionLike[]): { emoji: string; userIds: string[] }[] {
  const out: { emoji: string; userIds: string[] }[] = []
  for (const r of reactions) {
    const g = out.find((x) => x.emoji === r.emoji)
    if (g) {
      if (!g.userIds.includes(r.userId)) g.userIds.push(r.userId)
    } else out.push({ emoji: r.emoji, userIds: [r.userId] })
  }
  return out
}

/** 九宫格的列数：1 张单独大图，2 / 4 张两列，其余三列 */
export function gridColumns(count: number): 1 | 2 | 3 {
  if (count <= 1) return 1
  if (count === 2 || count === 4) return 2
  return 3
}

/** "2026-10" → "2026 年 10 月" */
export function monthTitle(month: string): string {
  const [y, m] = month.split('-')
  return `${y} 年 ${Number(m)} 月`
}

/** 动态正文的最大长度（与服务端一致） */
export const MAX_POST_TEXT = 5000
export const MAX_COMMENT_TEXT = 2000
export const MAX_MEDIA = 9
/** 点赞可选的表情（与聊天的快捷表情一致），第一个是默认的"赞" */
export const REACTIONS = ['❤️', '👍', '😂', '😮', '😢', '🙏', '🎉', '👏'] as const
