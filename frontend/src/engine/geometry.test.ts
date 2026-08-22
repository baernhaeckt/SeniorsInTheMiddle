import { describe, expect, it } from 'vitest'
import { easeInOut, edgeX, laneY, originFor, slotFor } from './geometry'
import type { Stage } from './store'

const STAGES: Stage[] = [
  'ingress',
  'inspect',
  'redact',
  'egress',
  'thinking',
  'return',
  'rehydrate',
  'deliver',
  'done',
]

describe('slotFor', () => {
  it.each([1200, 600])('returns a slot inside the band at width %d for every stage', (width) => {
    for (const stage of STAGES) {
      for (const kind of ['request', 'response'] as const) {
        const slot = slotFor(stage, kind, width, 300)
        expect(slot.x).toBeGreaterThanOrEqual(0)
        expect(slot.x).toBeLessThanOrEqual(width)
        expect(slot.y).toBe(laneY(kind, 300))
        expect(slot.travelMs).toBeGreaterThan(0)
      }
    }
  })

  it('moves a request left to right through the gate', () => {
    const stages: Stage[] = ['ingress', 'redact', 'egress', 'thinking']
    const xs = stages.map((stage) => slotFor(stage, 'request', 1000, 300).x)
    expect(xs).toEqual([...xs].sort((a, b) => a - b))
    expect(slotFor('redact', 'request', 1000, 300).docked).toBe(true)
    expect(slotFor('egress', 'request', 1000, 300).tone).toBe('cool')
  })

  it('moves a response right to left', () => {
    const stages: Stage[] = ['return', 'rehydrate', 'deliver']
    const xs = stages.map((stage) => slotFor(stage, 'response', 1000, 300).x)
    expect(xs).toEqual([...xs].sort((a, b) => b - a))
    expect(slotFor('rehydrate', 'response', 1000, 300).docked).toBe(true)
  })

  it('holds at the door while inspecting', () => {
    expect(slotFor('inspect', 'request', 1000, 300).x).toBe(
      slotFor('ingress', 'request', 1000, 300).x,
    )
  })
})

describe('edges and easing', () => {
  it('uses a wider margin on narrow bands', () => {
    expect(edgeX(1000) / 1000).toBeLessThan(edgeX(600) / 600)
  })

  it('originFor starts a request on the left and a response on the right', () => {
    expect(originFor('request', 1000, 300).x).toBeLessThan(500)
    expect(originFor('response', 1000, 300).x).toBeGreaterThan(500)
  })

  it('easeInOut is bounded and symmetric', () => {
    expect(easeInOut(0)).toBe(0)
    expect(easeInOut(1)).toBe(1)
    expect(easeInOut(0.5)).toBeCloseTo(0.5)
    expect(easeInOut(0.25)).toBeCloseTo(1 - easeInOut(0.75))
  })
})
