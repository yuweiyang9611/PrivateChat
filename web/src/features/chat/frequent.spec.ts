import { describe, expect, it } from 'vitest'
import { frequentContacts } from './frequent'

const ME = 'me'
const member = (id: string, disabled = false) => ({ id, disabled })
const direct = (other: string, lastMessageAt: string | null) => ({ title: null, memberIds: [ME, other], lastMessageAt })

describe('长辈模式首页：一键拨打放谁', () => {
  const members = [member(ME), member('son'), member('daughter'), member('grandson'), member('gone', true)]

  it('最近单聊过的人排前面', () => {
    const picked = frequentContacts(
      [direct('son', '2026-11-01T08:00:00Z'), direct('daughter', '2026-11-20T08:00:00Z'), direct('grandson', '2026-10-01T08:00:00Z')],
      members, ME, 2,
    )
    expect(picked.map((m) => m.id)).toEqual(['daughter', 'son'])
  })

  it('聊过的人不够数：按名单顺序补上其他家人', () => {
    const picked = frequentContacts([direct('grandson', '2026-11-20T08:00:00Z')], members, ME, 3)
    expect(picked.map((m) => m.id)).toEqual(['grandson', 'son', 'daughter'])
  })

  it('一个人都没聊过：就是名单里的前几个', () => {
    expect(frequentContacts([], members, ME, 2).map((m) => m.id)).toEqual(['son', 'daughter'])
  })

  it('群聊、还没说过话的单聊不算"最近联系"', () => {
    const picked = frequentContacts(
      [
        { title: '全家群', memberIds: [ME, 'grandson', 'son'], lastMessageAt: '2026-11-21T08:00:00Z' },
        { title: null, memberIds: [ME, 'grandson', 'daughter'], lastMessageAt: '2026-11-21T09:00:00Z' },
        direct('grandson', null),
        direct('daughter', '2026-11-01T08:00:00Z'),
      ],
      members, ME, 1,
    )
    expect(picked.map((m) => m.id)).toEqual(['daughter'])
  })

  it('自己和停用的成员不出现', () => {
    const picked = frequentContacts([direct('gone', '2026-11-21T08:00:00Z'), direct(ME, '2026-11-21T09:00:00Z')], members, ME, 5)
    expect(picked.map((m) => m.id)).toEqual(['son', 'daughter', 'grandson'])
  })

  it('家里只有自己：没有人可拨', () => {
    expect(frequentContacts([], [member(ME)], ME, 2)).toEqual([])
  })
})
