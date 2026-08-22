import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PROTOCOL_VERSION, type ServerEvent } from '../protocol/types'
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

    // The first exchange opens at ~1.3s; its privacy verdict lands ~10.6-11.5s later.
    vi.advanceTimersByTime(14_000)
    transport.stop()

    const hello = events[0]
    expect(hello?.type).toBe('hello')
    if (hello?.type === 'hello') {
      expect(hello.version).toBe(PROTOCOL_VERSION)
      expect(hello.policy.services.pii).toBe('ok')
    }
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
      'privacy.assessed',
    ])
    const detection = events.find(
      (event) => event.type === 'detection.completed' && event.exchangeId === id,
    )
    if (detection?.type === 'detection.completed') {
      expect(detection.entities.length).toBeGreaterThan(0)
      expect(Object.keys(detection.typeFrequencies).length).toBeGreaterThan(0)
      expect(detection.riskScoreMean).toBeGreaterThan(0)
    }
    const delivered = events.find(
      (event) => event.type === 'exchange.delivered' && event.exchangeId === id,
    )
    if (delivered?.type === 'exchange.delivered') expect(delivered.timing).toBeDefined()
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
