import { describe, expect, it } from 'vitest'
import { initialState, reduce } from '../engine/store'
import treated from './fixtures/treated-exchange.json'
import { PROTOCOL_VERSION, parseServerEvent } from './types'

const FIXTURES: Record<string, unknown[]> = { 'treated-exchange': treated }

describe('parseServerEvent', () => {
  it.each(Object.entries(FIXTURES))('accepts every frame in %s', (_, frames) => {
    for (const frame of frames) {
      const result = parseServerEvent(JSON.stringify(frame))
      expect(result.ok, JSON.stringify(frame)).toBe(true)
    }
  })

  it.each(Object.entries(FIXTURES))('reduces %s end to end', (_, frames) => {
    let state = initialState
    for (const frame of frames) {
      const result = parseServerEvent(JSON.stringify(frame))
      if (!result.ok) throw new Error(result.detail)
      state = reduce(state, result.event)
    }
    expect(state.protocolVersion).toBe(PROTOCOL_VERSION)
    expect(state.traffic).toHaveLength(2)
    expect(state.exchanges[0]?.stage).toBe('deliver')
    expect(state.vault).toHaveLength(2)
  })

  it('rejects non-text frames', () => {
    expect(parseServerEvent(new ArrayBuffer(4))).toMatchObject({ ok: false, reason: 'not-text' })
  })

  it('rejects text that is not JSON', () => {
    expect(parseServerEvent('{nope')).toMatchObject({ ok: false, reason: 'not-json' })
  })

  it('rejects JSON that is not an object', () => {
    expect(parseServerEvent('42')).toMatchObject({ ok: false, reason: 'not-an-object' })
    expect(parseServerEvent('null')).toMatchObject({ ok: false, reason: 'not-an-object' })
  })

  it('rejects an unknown type instead of guessing', () => {
    const result = parseServerEvent(JSON.stringify({ type: 'request.vanished', requestId: 'r' }))
    expect(result).toMatchObject({ ok: false, reason: 'invalid' })
  })

  it('rejects a known type with a missing field and names the field', () => {
    const result = parseServerEvent(JSON.stringify({ type: 'hello', version: 3 }))
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.detail).toContain('hello at proxy')
  })

  it('rejects a hello without the policy block', () => {
    const { policy: _dropped, ...frame } = treated[0] as Record<string, unknown>
    const result = parseServerEvent(JSON.stringify(frame))
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.detail).toContain('hello at policy')
  })

  it('fills in defaults for an entity and a detection without the v3 extras', () => {
    const detection = treated.find((frame) => frame.type === 'detection.completed') as Record<
      string,
      unknown
    >
    const {
      riskScoreMean: _r,
      typeFrequencies: _t,
      suppressed: _s,
      nearMisses: _n,
      ...rest
    } = detection
    const frame = {
      ...rest,
      entities: [
        {
          id: 'e1',
          kind: 'PERSON',
          value: 'Rosmarie Studer',
          token: '[PERSON_1]',
          start: 9,
          end: 24,
          confidence: 0.97,
        },
      ],
    }
    const result = parseServerEvent(JSON.stringify(frame))
    expect(result.ok).toBe(true)
    if (result.ok && result.event.type === 'detection.completed') {
      expect(result.event.entities[0]).toMatchObject({
        informationType: '',
        riskLevel: 0,
        hipaaCategory: '',
      })
      expect(result.event.riskScoreMean).toBeUndefined()
      expect(result.event.typeFrequencies).toEqual({})
      expect(result.event.suppressed).toBe(0)
      expect(result.event.nearMisses).toEqual([])
    }
  })

  it('accepts a privacy verdict and leaves the stage where it was', () => {
    let state = initialState
    for (const frame of treated) {
      const result = parseServerEvent(JSON.stringify(frame))
      if (!result.ok) throw new Error(result.detail)
      state = reduce(state, result.event)
    }
    expect(state.exchanges[0]?.privacy?.status).toBe('ok')
    expect(state.exchanges[0]?.stage).toBe('deliver')
  })

  it('rejects a known type with a wrong value', () => {
    const frame = { ...(treated[1] as object), treatment: 'shredded' }
    const result = parseServerEvent(JSON.stringify(frame))
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.detail).toContain('treatment')
  })

  it('keeps optional fields optional', () => {
    const { contentType: _dropped, ...frame } = treated[1] as Record<string, unknown>
    const result = parseServerEvent(JSON.stringify(frame))
    expect(result.ok).toBe(true)
    if (result.ok && result.event.type === 'request.observed') {
      expect(result.event.contentType).toBeUndefined()
    }
  })
})
