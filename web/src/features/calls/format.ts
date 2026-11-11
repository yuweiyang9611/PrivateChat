import type { CallDto, CallEndReason, CallType } from './api'

/** 75 → 01:15；3725 → 1:02:05 */
export function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const pad = (n: number): string => String(n).padStart(2, '0')
  return h > 0 ? `${h}:${pad(m)}:${pad(s % 60)}` : `${pad(m)}:${pad(s % 60)}`
}

/** 通话记录消息的载荷（messages.payload），由服务端在通话结束时写入 */
export interface CallMessagePayload {
  callId: string
  callType: CallType
  reason: CallEndReason
  durationSeconds: number
  participantIds: string[]
}

export function parseCallPayload(payload: string | null | undefined): CallMessagePayload | null {
  if (!payload) return null
  try {
    const p = JSON.parse(payload) as Partial<CallMessagePayload>
    return typeof p.callId === 'string' && typeof p.reason === 'string' && typeof p.callType === 'string'
      ? { callId: p.callId, callType: p.callType, reason: p.reason, durationSeconds: Number(p.durationSeconds) || 0, participantIds: p.participantIds ?? [] }
      : null
  } catch {
    return null
  }
}

/** 一条通话记录对"我"来说是什么：打出去的、接到的、没接到的 */
export type CallOutcome = 'completed' | 'missed-by-me' | 'no-answer' | 'declined-by-me' | 'declined-by-peer' | 'cancelled'

export function callOutcome(reason: CallEndReason, startedBy: string, me: string | undefined): CallOutcome {
  const mine = startedBy === me
  switch (reason) {
    case 'Completed': return 'completed'
    case 'Missed': return mine ? 'no-answer' : 'missed-by-me'
    case 'Declined': return mine ? 'declined-by-peer' : 'declined-by-me'
    default: return mine ? 'cancelled' : 'missed-by-me'
  }
}

/** i18n 键与参数：通话记录的一行说明 */
export function outcomeText(outcome: CallOutcome, durationSeconds: number): { key: string; params?: Record<string, string> } {
  if (outcome === 'completed') return { key: 'calls.outcome.completed', params: { d: formatDuration(durationSeconds) } }
  const map: Record<Exclude<CallOutcome, 'completed'>, string> = {
    'missed-by-me': 'calls.outcome.missed',
    'no-answer': 'calls.outcome.noAnswer',
    'declined-by-me': 'calls.outcome.declined',
    'declined-by-peer': 'calls.outcome.declinedByPeer',
    cancelled: 'calls.outcome.cancelled',
  }
  return { key: map[outcome] }
}

/** 已结束通话的时长（秒）；没接通过为 0 */
export function callDuration(c: Pick<CallDto, 'answeredAt' | 'endedAt'>): number {
  if (!c.answeredAt || !c.endedAt) return 0
  return Math.max(0, (new Date(c.endedAt).getTime() - new Date(c.answeredAt).getTime()) / 1000)
}
