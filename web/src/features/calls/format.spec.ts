import { describe, expect, it } from 'vitest'
import { callDuration, callOutcome, formatDuration, outcomeText, parseCallPayload } from './format'
import { SerialQueue } from './rtc/queue'

describe('通话记录的显示', () => {
  it('时长格式', () => {
    expect(formatDuration(0)).toBe('00:00')
    expect(formatDuration(75.9)).toBe('01:15')
    expect(formatDuration(3725)).toBe('1:02:05')
    expect(formatDuration(-3)).toBe('00:00')
  })

  it('同一条记录，打出去的人和接的人看到的说法不同', () => {
    expect(callOutcome('Missed', 'dad', 'dad')).toBe('no-answer')
    expect(callOutcome('Missed', 'dad', 'me')).toBe('missed-by-me')
    expect(callOutcome('Declined', 'dad', 'dad')).toBe('declined-by-peer')
    expect(callOutcome('Declined', 'dad', 'me')).toBe('declined-by-me')
    // 对方响了几声自己挂了：对我来说也是未接来电
    expect(callOutcome('Cancelled', 'dad', 'me')).toBe('missed-by-me')
    expect(callOutcome('Cancelled', 'dad', 'dad')).toBe('cancelled')
    expect(callOutcome('Completed', 'dad', 'me')).toBe('completed')
    expect(outcomeText('completed', 75)).toEqual({ key: 'calls.outcome.completed', params: { d: '01:15' } })
    expect(outcomeText('missed-by-me', 0)).toEqual({ key: 'calls.outcome.missed' })
  })

  it('载荷解析：坏数据返回 null，由调用方退回显示正文', () => {
    const ok = parseCallPayload(JSON.stringify({ callId: 'c1', callType: 'Video', reason: 'Completed', durationSeconds: 62, participantIds: ['a', 'b'] }))
    expect(ok).toEqual({ callId: 'c1', callType: 'Video', reason: 'Completed', durationSeconds: 62, participantIds: ['a', 'b'] })
    expect(parseCallPayload(null)).toBeNull()
    expect(parseCallPayload('{oops')).toBeNull()
    expect(parseCallPayload('{"callId":1}')).toBeNull()
  })

  it('通话时长从接通算起；没接通过为 0', () => {
    expect(callDuration({ answeredAt: '2026-11-11T10:00:00Z', endedAt: '2026-11-11T10:01:30Z' })).toBe(90)
    expect(callDuration({ answeredAt: null, endedAt: '2026-11-11T10:01:30Z' })).toBe(0)
  })
})

describe('串行队列', () => {
  it('一个任务完全结束后才开始下一个，失败的任务不影响后面的', async () => {
    const q = new SerialQueue()
    const log: string[] = []
    const slow = q.run(async () => {
      log.push('a:start')
      await new Promise((r) => setTimeout(r, 20))
      log.push('a:end')
      return 1
    })
    const failing = q.run(async () => {
      log.push('b')
      throw new Error('boom')
    })
    const last = q.run(async () => {
      log.push('c')
      return 3
    })
    expect(await slow).toBe(1)
    await expect(failing).rejects.toThrow('boom')
    expect(await last).toBe(3)
    expect(log).toEqual(['a:start', 'a:end', 'b', 'c'])
  })

  it('关闭后排队中的任务不再执行', async () => {
    const q = new SerialQueue()
    let ran = false
    let started!: () => void
    const running = new Promise<void>((r) => { started = r })
    const first = q.run(() => {
      started()
      return new Promise<void>((r) => setTimeout(r, 10))
    })
    const queued = q.run(async () => { ran = true })
    const outcome = queued.then(() => 'ran', (e: Error) => e.message)
    await running
    q.close()
    // 已经在跑的那个照常跑完
    await first
    expect(await outcome).toBe('queue closed')
    expect(ran).toBe(false)
  })
})
