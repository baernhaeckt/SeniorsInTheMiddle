import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ServerEvent } from '../protocol/types'
import { createDemoTransport } from './demoTransport'

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('createDemoTransport', () => {
  it('says hello, then plays a full treated lifecycle in protocol order', () => {
    const transport = createDemoTransport()
    const events: ServerEvent[] = []
    transport.onEvent((event) => events.push(event))
    transport.start()

    vi.advanceTimersByTime(12_000)
    transport.stop()

    expect(events[0]?.type).toBe('hello')
    const opened = events.find((event) => event.type === 'exchange.opened')
    expect(opened).toBeDefined()
    const id = opened && 'exchangeId' in opened ? opened.exchangeId : ''
    const lifecycle = events
      .filter((event) => 'exchangeId' in event && event.exchangeId === id && event.type !== 'log')
      .map((event) => event.type)
    expect(lifecycle).toEqual([
      'request.observed',
      'exchange.opened',
      'detection.completed',
      'redaction.completed',
      'upstream.dispatched',
      'upstream.responded',
      'rehydration.completed',
      'exchange.delivered',
    ])
    const untreated = events.some(
      (event) => event.type === 'request.observed' && event.treatment !== 'treated',
    )
    expect(untreated).toBe(true)
  })

  it('stop clears every pending timer and reports closed', () => {
    const transport = createDemoTransport()
    const events: ServerEvent[] = []
    const statuses: string[] = []
    transport.onEvent((event) => events.push(event))
    transport.onStatus((status) => statuses.push(status.state))
    transport.start()
    vi.advanceTimersByTime(2_000)
    transport.stop()
    const count = events.length
    vi.advanceTimersByTime(30_000)
    expect(events).toHaveLength(count)
    expect(statuses[statuses.length - 1]).toBe('closed')
    expect(vi.getTimerCount()).toBe(0)
  })
})
