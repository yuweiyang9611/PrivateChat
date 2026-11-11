/**
 * 串行队列：同一条 Cloudflare 会话上的变更（发布、订阅、关闭、协商）必须一个做完再做下一个。
 * "做完"包括整个 SDP 往返；事件处理器、定时器、按钮点击都可能同时触发，所以都从这里排队。
 * 前一个任务失败不影响后面的任务。
 */
export class SerialQueue {
  private tail: Promise<unknown> = Promise.resolve()
  private closed = false

  run<T>(task: () => Promise<T>): Promise<T> {
    const next = this.tail.then(() => {
      if (this.closed) throw new QueueClosedError()
      return task()
    })
    this.tail = next.catch(() => undefined)
    return next
  }

  /** 关闭后排队中的任务不再执行（已经在跑的那个跑完为止） */
  close(): void {
    this.closed = true
  }

  /** 当前排着的任务全部结束 */
  idle(): Promise<void> {
    return this.tail.then(() => undefined)
  }
}

export class QueueClosedError extends Error {
  constructor() {
    super('queue closed')
    this.name = 'QueueClosedError'
  }
}
