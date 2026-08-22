import { describe, expect, it } from 'vitest'
import treated from '../protocol/fixtures/treated-exchange.json'
import type { Entity, EventOf, ServerEvent } from '../protocol/types'
import {
  LIMITS,
  activeExchange,
  createStore,
  evictExchanges,
  initialState,
  median,
  mergeVault,
  reduce,
  shownExchange,
  type AppState,
  type Exchange,
  type Stage,
} from './store'

const frames = treated as ServerEvent[]
const byType = <T extends ServerEvent['type']>(type: T): EventOf<T> => {
  const frame = frames.find((item) => item.type === type)
  if (!frame) throw new Error(`no fixture frame of type ${type}`)
  return frame as EventOf<T>
}

function replay(events: ServerEvent[], from: AppState = initialState): AppState {
  let seq = 0
  return events.reduce((state, event) => {
    if (event.type === 'request.observed') seq += 1
    return reduce(state, event, seq)
  }, from)
}

function exchange(id: string, stage: Stage, extra: Partial<Exchange> = {}): Exchange {
  return {
    id,
    requestId: `r-${id}`,
    openedAt: 0,
    stage,
    stageAt: 0,
    clientLabel: 'c',
    method: 'POST',
    scheme: 'https',
    host: 'h',
    path: '/',
    contentType: 'application/json',
    requestBody: '{}',
    entities: [],
    ...extra,
  }
}

describe('reduce: happy path', () => {
  it('walks the full stage path for a treated exchange', () => {
    const seen: Stage[] = []
    let state = initialState
    for (const frame of frames) {
      state = reduce(state, frame, 1)
      const current = state.exchanges[0]?.stage
      if (current && seen[seen.length - 1] !== current) seen.push(current)
    }
    expect(seen).toEqual([
      'ingress',
      'inspect',
      'redact',
      'egress',
      'return',
      'rehydrate',
      'deliver',
    ])
  })

  it('settles egress into thinking and deliver into done, nothing else', () => {
    const opened = replay(frames.slice(0, 9)) // up to upstream.dispatched
    expect(opened.exchanges[0]?.stage).toBe('egress')
    const settled = reduce(opened, { type: 'view.settle', exchangeId: 'x-1', at: 5 })
    expect(settled.exchanges[0]?.stage).toBe('thinking')
    expect(settled.exchanges[0]?.stageAt).toBe(5)
    // Settling a stage that does not settle is a no-op, same reference.
    expect(reduce(settled, { type: 'view.settle', exchangeId: 'x-1', at: 6 })).toBe(settled)

    const delivered = replay(frames)
    const done = reduce(delivered, { type: 'view.settle', exchangeId: 'x-1', at: 9 })
    expect(done.exchanges[0]?.stage).toBe('done')
  })

  it('counts metrics and stamps sequence numbers', () => {
    const state = replay(frames)
    expect(state.metrics).toEqual({
      requests: 2,
      treated: 1,
      identifiersHeld: 2,
      latencies: [310],
    })
    expect(state.traffic.map((entry) => entry.seq)).toEqual([2, 1])
    expect(state.traffic[0]).toMatchObject({
      requestId: 'r-2',
      identifiers: 2,
      status: 201,
      durationMs: 310,
    })
  })

  it('records the protocol version and flags a mismatch in the log line', () => {
    const ok = reduce(initialState, byType('hello'))
    expect(ok.protocolVersion).toBe(2)
    expect(ok.lastLog).toContain('attached')
    const bad = reduce(initialState, { ...byType('hello'), version: 3 })
    expect(bad.protocolVersion).toBe(3)
    expect(bad.lastLog).toContain('expects v2')
  })

  it('ignores stage events for an unknown exchange without throwing', () => {
    const state = reduce(initialState, { ...byType('redaction.completed'), exchangeId: 'ghost' })
    expect(state.exchanges).toEqual([])
  })

  it('returns the same reference when a view action changes nothing', () => {
    expect(reduce(initialState, { type: 'view.pin', exchangeId: null })).toBe(initialState)
    expect(reduce(initialState, { type: 'view.hover', token: null })).toBe(initialState)
  })

  it('reset keeps the link status', () => {
    const state = reduce(replay(frames), {
      type: 'view.link',
      link: { state: 'live', endpoint: 'ws://x' },
    })
    const fresh = reduce(state, { type: 'view.reset' })
    expect(fresh.traffic).toEqual([])
    expect(fresh.link).toEqual({ state: 'live', endpoint: 'ws://x' })
  })
})

describe('caps', () => {
  it('caps the traffic list', () => {
    const observed = byType('request.observed')
    const many = Array.from({ length: LIMITS.traffic + 10 }, (_, i) => ({
      ...observed,
      requestId: `r-${i}`,
    }))
    expect(replay(many).traffic).toHaveLength(LIMITS.traffic)
  })

  it('evicts finished exchanges before in-flight ones', () => {
    const list = [
      exchange('new', 'ingress'),
      exchange('a', 'thinking'),
      exchange('b', 'done'),
      exchange('c', 'return'),
      exchange('d', 'done'),
    ]
    expect(evictExchanges(list, 3).map((item) => item.id)).toEqual(['new', 'a', 'c'])
  })

  it('drops the oldest in-flight exchange only when nothing finished is left', () => {
    const list = [exchange('new', 'ingress'), exchange('a', 'thinking'), exchange('b', 'return')]
    expect(evictExchanges(list, 2).map((item) => item.id)).toEqual(['new', 'a'])
  })

  it('keeps the latency window bounded', () => {
    const delivered = byType('exchange.delivered')
    let state = replay(frames)
    for (let i = 0; i < LIMITS.latencies + 5; i += 1) {
      state = reduce(state, { ...delivered, totalMs: i })
    }
    expect(state.metrics.latencies).toHaveLength(LIMITS.latencies)
  })
})

describe('mergeVault', () => {
  const entity = (token: string, value = token): Entity => ({
    id: token,
    kind: 'PERSON',
    value,
    token,
    start: 0,
    end: 1,
    confidence: 1,
  })

  it('adds new tokens at the front and bumps uses for repeats', () => {
    const once = mergeVault([], [entity('[PERSON_1]', 'A')], 1)
    const twice = mergeVault(once, [entity('[PERSON_2]', 'B'), entity('[PERSON_1]', 'A')], 2)
    expect(twice.map((record) => [record.token, record.uses])).toEqual([
      ['[PERSON_2]', 1],
      ['[PERSON_1]', 2],
    ])
    expect(twice[1]?.firstSeenAt).toBe(1)
  })

  it('returns the same reference for no entities', () => {
    const vault = mergeVault([], [entity('[X]')], 1)
    expect(mergeVault(vault, [], 2)).toBe(vault)
  })

  it('caps the vault', () => {
    const many = Array.from({ length: LIMITS.vault + 3 }, (_, i) => entity(`[T_${i}]`))
    expect(mergeVault([], many, 1)).toHaveLength(LIMITS.vault)
  })
})

describe('selectors', () => {
  it('median', () => {
    expect(median([])).toBeNull()
    expect(median([5, 1, 3])).toBe(3)
    expect(median([4, 1])).toBe(4)
  })

  it('activeExchange skips finished ones', () => {
    expect(activeExchange([exchange('a', 'done'), exchange('b', 'return')])?.id).toBe('b')
    expect(activeExchange([exchange('a', 'done')])).toBeNull()
  })

  it('shownExchange prefers pinned, then round-tripped, then newest', () => {
    const list = [
      exchange('a', 'ingress'),
      exchange('b', 'deliver', { responseBody: '{}' }),
      exchange('c', 'done', { responseBody: '{}' }),
    ]
    expect(shownExchange(list, 'c')?.id).toBe('c')
    expect(shownExchange(list, null)?.id).toBe('b')
    expect(shownExchange([exchange('a', 'ingress')], null)?.id).toBe('a')
    expect(shownExchange([], null)).toBeNull()
  })
})

describe('createStore', () => {
  it('notifies subscribers only when state changes', () => {
    const store = createStore()
    let calls = 0
    const off = store.subscribe(() => {
      calls += 1
    })
    store.pin(null) // no change
    store.pin('x')
    store.hover('t')
    store.hover('t') // no change
    expect(calls).toBe(2)
    off()
    store.pin(null)
    expect(calls).toBe(2)
  })

  it('is independent per instance', () => {
    const a = createStore()
    const b = createStore()
    a.apply(byType('request.observed'))
    expect(a.getSnapshot().traffic).toHaveLength(1)
    expect(b.getSnapshot().traffic).toHaveLength(0)
  })
})
