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
  let logSeq = 0
  return events.reduce((state, event) => {
    if (event.type === 'request.observed') seq += 1
    if (event.type === 'log') logSeq += 1
    return reduce(state, event, seq, logSeq)
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
    typeFrequencies: {},
    suppressed: 0,
    nearMisses: [],
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
      blocks: 1,
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
    expect(ok.protocolVersion).toBe(3)
    expect(ok.policy).toEqual(byType('hello').policy)
    expect(ok.logs[0]?.message).toContain('attached')
    expect(ok.logs[0]?.level).toBe('info')
    const bad = reduce(initialState, { ...byType('hello'), version: 99 })
    expect(bad.protocolVersion).toBe(99)
    expect(bad.logs[0]?.message).toContain('expects v3')
    expect(bad.logs[0]?.level).toBe('warn')
  })

  it('stores what detection reported beyond the entities', () => {
    const state = replay(frames)
    expect(state.exchanges[0]).toMatchObject({
      riskScoreMean: 0.98,
      typeFrequencies: { PERSON: 1, AHV: 1 },
      suppressed: 0,
      nearMisses: [{ kind: 'LOCATION', value: 'Bern', confidence: 0.42 }],
      restored: 1,
      timing: { bufferMs: 3, detectMs: 14, upstreamMs: 265, rehydrateMs: 2, overheadMs: 26 },
    })
  })

  it('privacy.assessed fills in the verdict without moving the stage', () => {
    const delivered = replay(frames.filter((frame) => frame.type !== 'privacy.assessed'))
    const before = delivered.exchanges[0]
    expect(before?.privacy).toBeUndefined()
    const assessed = reduce(delivered, byType('privacy.assessed'))
    const after = assessed.exchanges[0]
    expect(after?.stage).toBe(before?.stage)
    expect(after?.stageAt).toBe(before?.stageAt)
    expect(after?.privacy).toEqual({
      status: 'ok',
      risks: [{ token: '[PERSON_1]', probability: 1 }],
      maxProbability: 1,
      assessedMs: 2190,
      reason: undefined,
    })
    // Also lands on a settled exchange, which the late verdict usually meets.
    const done = reduce(delivered, { type: 'view.settle', exchangeId: 'x-1', at: 9 })
    expect(reduce(done, byType('privacy.assessed')).exchanges[0]?.stage).toBe('done')
  })

  it('privacy.assessed for an unknown exchange is a no-op', () => {
    const state = replay(frames)
    expect(reduce(state, { ...byType('privacy.assessed'), exchangeId: 'ghost' })).toBe(state)
  })

  it('keeps log lines newest first and counts blocks', () => {
    const log = (level: 'info' | 'warn' | 'block', message: string): EventOf<'log'> => ({
      type: 'log',
      at: 1,
      level,
      message,
    })
    const state = replay([log('info', 'one'), log('block', 'two'), log('block', 'three')])
    expect(state.logs.map((line) => [line.seq, line.message])).toEqual([
      [3, 'three'],
      [2, 'two'],
      [1, 'one'],
    ])
    expect(state.metrics.blocks).toBe(2)
  })

  it('caps the log', () => {
    const many: ServerEvent[] = Array.from({ length: LIMITS.logs + 5 }, (_, i) => ({
      type: 'log',
      at: i,
      level: 'info',
      message: `m${i}`,
    }))
    const state = replay(many)
    expect(state.logs).toHaveLength(LIMITS.logs)
    expect(state.logs[0]?.message).toBe(`m${LIMITS.logs + 4}`)
  })

  it('tallies devices from the traffic', () => {
    const state = replay(frames)
    expect(state.devices).toHaveLength(1)
    expect(state.devices[0]).toMatchObject({
      clientLabel: 'Tablet · Studer',
      clientIp: '192.168.1.44',
      seen: 2,
      treated: 1,
      identifiers: 2,
      maxRisk: 3,
      lastSeenAt: 1756000001020,
    })
  })

  it('keeps one line per device, most recent first', () => {
    const observed = byType('request.observed')
    const state = replay([
      { ...observed, requestId: 'a', clientLabel: 'A', clientIp: '10.0.0.1', at: 1 },
      { ...observed, requestId: 'b', clientLabel: 'B', clientIp: '10.0.0.2', at: 2 },
      { ...observed, requestId: 'c', clientLabel: 'A', clientIp: '10.0.0.1', at: 3 },
    ])
    expect(state.devices.map((device) => [device.clientLabel, device.seen])).toEqual([
      ['A', 2],
      ['B', 1],
    ])
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
    informationType: '',
    riskLevel: 0,
    hipaaCategory: '',
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
