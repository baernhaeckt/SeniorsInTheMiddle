import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { LinkStatus } from './types'
import { BACKOFF_MS, backoffFor, createWebSocketTransport } from './websocketTransport'

type Listener = (event: never) => void

class FakeWebSocket {
  static instances: FakeWebSocket[] = []
  static failConstructor = false
  readonly listeners = new Map<string, Set<Listener>>()
  closed = false

  constructor(public readonly url: string) {
    if (FakeWebSocket.failConstructor) throw new Error('bad url')
    FakeWebSocket.instances.push(this)
  }

  addEventListener(type: string, listener: Listener) {
    const set = this.listeners.get(type) ?? new Set()
    set.add(listener)
    this.listeners.set(type, set)
  }

  close() {
    this.closed = true
  }

  fire(type: string, event: unknown) {
    for (const listener of this.listeners.get(type) ?? []) listener(event as never)
  }

  open() {
    this.fire('open', {})
  }

  message(data: unknown) {
    this.fire('message', { data })
  }

  drop(code = 1006, reason = '') {
    this.fire('close', { code, reason })
  }
}

const latest = () => FakeWebSocket.instances[FakeWebSocket.instances.length - 1]
const HELLO = JSON.stringify({
  type: 'hello',
  version: 2,
  proxy: { name: 'p', region: 'r', mode: 'm', policy: 'x' },
})

function setup() {
  const transport = createWebSocketTransport('ws://proxy/stream', {
    WebSocketImpl: FakeWebSocket as unknown as typeof WebSocket,
    random: () => 0.5, // no jitter
    onRejected: vi.fn(),
  })
  const statuses: LinkStatus[] = []
  const events: unknown[] = []
  transport.onStatus((status) => statuses.push(status))
  transport.onEvent((event) => events.push(event))
  return { transport, statuses, events, last: () => statuses[statuses.length - 1] }
}

beforeEach(() => {
  vi.useFakeTimers()
  FakeWebSocket.instances = []
  FakeWebSocket.failConstructor = false
})

afterEach(() => {
  vi.useRealTimers()
})

describe('backoffFor', () => {
  it('follows the table and saturates', () => {
    expect(backoffFor(0, () => 0.5)).toBe(BACKOFF_MS[0])
    expect(backoffFor(99, () => 0.5)).toBe(BACKOFF_MS[BACKOFF_MS.length - 1])
  })

  it('jitters by up to ±20 %', () => {
    expect(backoffFor(2, () => 1)).toBe(2400)
    expect(backoffFor(2, () => 0)).toBe(1600)
  })
})

describe('createWebSocketTransport', () => {
  it('reports idle before start, then connecting, then live', () => {
    const { transport, statuses, last } = setup()
    expect(statuses[0]?.state).toBe('idle')
    transport.start()
    expect(last()?.state).toBe('connecting')
    latest()?.open()
    expect(last()?.state).toBe('live')
  })

  it('forwards valid frames and drops invalid ones with a count', () => {
    const { transport, events, last } = setup()
    transport.start()
    const socket = latest()
    socket?.open()
    socket?.message(HELLO)
    socket?.message('{"type":"hello"}')
    socket?.message(new ArrayBuffer(1))
    expect(events).toHaveLength(1)
    expect(last()?.dropped).toBe(2)
    expect(last()?.state).toBe('live')
  })

  it('retries with backoff after a close, and resets the attempt once open', () => {
    const { transport, last } = setup()
    transport.start()
    latest()?.drop(1006, 'gone')
    expect(last()).toMatchObject({ state: 'retrying', attempt: 1, detail: 'gone' })
    expect(FakeWebSocket.instances).toHaveLength(1)

    vi.advanceTimersByTime(BACKOFF_MS[0])
    expect(FakeWebSocket.instances).toHaveLength(2)
    latest()?.drop(1006)
    expect(last()?.attempt).toBe(2)
    vi.advanceTimersByTime(BACKOFF_MS[1] - 1)
    expect(FakeWebSocket.instances).toHaveLength(2)
    vi.advanceTimersByTime(1)
    expect(FakeWebSocket.instances).toHaveLength(3)

    latest()?.open()
    expect(last()?.state).toBe('live')
    latest()?.drop(1006)
    expect(last()?.attempt).toBe(1)
  })

  it('retries when the constructor itself throws', () => {
    const { transport, last } = setup()
    FakeWebSocket.failConstructor = true
    transport.start()
    expect(last()).toMatchObject({ state: 'retrying', detail: 'bad url' })
    FakeWebSocket.failConstructor = false
    vi.advanceTimersByTime(BACKOFF_MS[0])
    expect(FakeWebSocket.instances).toHaveLength(1)
  })

  it('stop cancels a pending retry and reports closed', () => {
    const { transport, last } = setup()
    transport.start()
    latest()?.drop(1006)
    transport.stop()
    expect(last()?.state).toBe('closed')
    vi.advanceTimersByTime(60_000)
    expect(FakeWebSocket.instances).toHaveLength(1)
  })

  it('stop on a live socket closes it and ignores its close event', () => {
    const { transport, last } = setup()
    transport.start()
    const socket = latest()
    socket?.open()
    transport.stop()
    expect(socket?.closed).toBe(true)
    socket?.drop(1000, 'bye')
    expect(last()?.state).toBe('closed')
  })

  it('starts over after stop', () => {
    const { transport, last } = setup()
    transport.start()
    transport.stop()
    transport.start()
    expect(FakeWebSocket.instances).toHaveLength(2)
    expect(last()?.state).toBe('connecting')
  })

  it('start is idempotent while running', () => {
    const { transport } = setup()
    transport.start()
    transport.start()
    expect(FakeWebSocket.instances).toHaveLength(1)
  })
})
