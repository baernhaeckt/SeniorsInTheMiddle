import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BACKOFF_MS, backoffFor, createSignalRTransport, type HubLike } from './signalrTransport'
import type { LinkStatus } from './types'

/**
 * Stands in for a HubConnection. The real client needs a WebSocket and a fetch, neither of
 * which jsdom has, so the transport takes its connection from a factory instead.
 */
class FakeHub implements HubLike {
  static instances: FakeHub[] = []
  static failFactory = false

  private handlers = new Map<string, (payload: never) => void>()
  private closeHandlers: ((error?: Error) => void)[] = []
  private resolveStart?: () => void
  private rejectStart?: (error: Error) => void

  stopped = false
  readonly started: Promise<void>

  constructor(public readonly url: string) {
    this.started = new Promise<void>((resolve, reject) => {
      this.resolveStart = resolve
      this.rejectStart = reject
    })
    // Nothing awaits a rejected start except the transport; keep vitest quiet until then.
    this.started.catch(() => undefined)
  }

  start(): Promise<void> {
    return this.started
  }

  stop(): Promise<void> {
    this.stopped = true
    return Promise.resolve()
  }

  on(method: string, handler: (payload: never) => void) {
    this.handlers.set(method, handler)
  }

  onclose(handler: (error?: Error) => void) {
    this.closeHandlers.push(handler)
  }

  /** The server accepted the connection. */
  connect() {
    this.resolveStart?.()
    return this.settle()
  }

  /** The proxy is not up. A start that never connected does not raise onclose. */
  refuse(message = 'Failed to start the connection') {
    this.rejectStart?.(new Error(message))
    return this.settle()
  }

  drop(message?: string) {
    for (const handler of this.closeHandlers) handler(message ? new Error(message) : undefined)
  }

  emit(payload: unknown) {
    this.handlers.get('event')?.(payload as never)
  }

  /** Let the promise callbacks the transport attached run. */
  private async settle() {
    await Promise.resolve()
    await Promise.resolve()
  }
}

const latest = () => FakeHub.instances[FakeHub.instances.length - 1]
const HELLO = JSON.stringify({
  type: 'hello',
  version: 2,
  proxy: { name: 'p', region: 'r', mode: 'm', policy: 'x' },
})

function setup() {
  const transport = createSignalRTransport('http://proxy:8080/hub/telemetry', {
    connectionFactory: (url) => {
      if (FakeHub.failFactory) throw new Error('bad url')
      const hub = new FakeHub(url)
      FakeHub.instances.push(hub)
      return hub
    },
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
  FakeHub.instances = []
  FakeHub.failFactory = false
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

describe('createSignalRTransport', () => {
  it('reports the current status synchronously, before anything is subscribed', () => {
    const { statuses } = setup()
    expect(statuses[0]).toMatchObject({
      state: 'idle',
      endpoint: 'http://proxy:8080/hub/telemetry',
    })
  })

  it('goes connecting, then live once the hub accepts', async () => {
    const { transport, last } = setup()
    transport.start()
    expect(last()?.state).toBe('connecting')
    await latest()?.connect()
    expect(last()?.state).toBe('live')
  })

  it('forwards valid frames and drops invalid ones with a count', async () => {
    const { transport, events, last } = setup()
    transport.start()
    const hub = latest()
    await hub?.connect()

    hub?.emit(HELLO)
    hub?.emit('{"type":"hello"}') // missing version and proxy
    hub?.emit({ type: 'hello' }) // an object, not the JSON string the hub sends

    expect(events).toHaveLength(1)
    expect(last()?.dropped).toBe(2)
    expect(last()?.state).toBe('live')
  })

  it('retries when the very first connection is refused', async () => {
    // withAutomaticReconnect would not cover this: the proxy is simply not up yet.
    const { transport, last } = setup()
    transport.start()
    await latest()?.refuse('Cannot reach the hub')

    expect(last()).toMatchObject({ state: 'retrying', attempt: 1, detail: 'Cannot reach the hub' })
    expect(FakeHub.instances).toHaveLength(1)

    vi.advanceTimersByTime(BACKOFF_MS[0])
    expect(FakeHub.instances).toHaveLength(2)
  })

  it('retries with backoff after a drop, and resets the attempt once live', async () => {
    const { transport, last } = setup()
    transport.start()
    await latest()?.connect()

    latest()?.drop('gone')
    expect(last()).toMatchObject({ state: 'retrying', attempt: 1, detail: 'gone' })

    vi.advanceTimersByTime(BACKOFF_MS[0])
    expect(FakeHub.instances).toHaveLength(2)
    await latest()?.refuse()
    expect(last()?.attempt).toBe(2)

    vi.advanceTimersByTime(BACKOFF_MS[1] - 1)
    expect(FakeHub.instances).toHaveLength(2)
    vi.advanceTimersByTime(1)
    expect(FakeHub.instances).toHaveLength(3)

    await latest()?.connect()
    expect(last()?.state).toBe('live')
    latest()?.drop()
    expect(last()?.attempt).toBe(1)
  })

  it('retries when building the connection itself throws', () => {
    const { transport, last } = setup()
    FakeHub.failFactory = true
    transport.start()
    expect(last()).toMatchObject({ state: 'retrying', detail: 'bad url' })

    FakeHub.failFactory = false
    vi.advanceTimersByTime(BACKOFF_MS[0])
    expect(FakeHub.instances).toHaveLength(1)
  })

  it('stop cancels a pending retry and reports closed', async () => {
    const { transport, last } = setup()
    transport.start()
    await latest()?.refuse()

    transport.stop()
    expect(last()?.state).toBe('closed')
    vi.advanceTimersByTime(60_000)
    expect(vi.getTimerCount()).toBe(0)
    expect(FakeHub.instances).toHaveLength(1)
  })

  it('stop on a live connection closes it and ignores the close that follows', async () => {
    const { transport, last } = setup()
    transport.start()
    const hub = latest()
    await hub?.connect()

    transport.stop()
    expect(hub?.stopped).toBe(true)
    hub?.drop('bye')
    expect(last()?.state).toBe('closed')
  })

  it('starts over after stop', () => {
    const { transport, last } = setup()
    transport.start()
    transport.stop()
    transport.start()
    expect(FakeHub.instances).toHaveLength(2)
    expect(last()?.state).toBe('connecting')
  })

  it('start is idempotent while running', () => {
    const { transport } = setup()
    transport.start()
    transport.start()
    expect(FakeHub.instances).toHaveLength(1)
  })
})
