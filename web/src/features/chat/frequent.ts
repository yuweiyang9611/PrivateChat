/** 挑"常联系的人"要用到的会话字段 */
export interface DirectLike {
  title?: string | null
  memberIds: string[]
  lastMessageAt?: string | null
}

/** 挑"常联系的人"要用到的成员字段 */
export interface MemberLike {
  id: string
  disabled: boolean
}

/**
 * 长辈模式首页"一键拨打"放谁：最近单聊过的人排前面（聊过天、打过电话都会让那个单聊变新），
 * 不够数再按名单顺序补上其他家人。自己、停用的成员不算。
 */
export function frequentContacts<M extends MemberLike>(conversations: DirectLike[], members: M[], me: string | undefined, limit: number): M[] {
  const callable = new Map(members.filter((m) => m.id !== me && !m.disabled).map((m) => [m.id, m]))
  const picked: M[] = []
  const direct = conversations
    // 单聊：没有群名、正好两个人、其中一个是我
    .filter((c) => !c.title && c.memberIds.length === 2 && (!me || c.memberIds.includes(me)) && c.lastMessageAt)
    .sort((a, b) => (b.lastMessageAt ?? '').localeCompare(a.lastMessageAt ?? ''))
  for (const c of direct) {
    const other = c.memberIds.find((id) => id !== me)
    const member = other ? callable.get(other) : undefined
    if (member && !picked.includes(member)) picked.push(member)
    if (picked.length >= limit) return picked
  }
  for (const member of callable.values()) {
    if (!picked.includes(member)) picked.push(member)
    if (picked.length >= limit) break
  }
  return picked
}
