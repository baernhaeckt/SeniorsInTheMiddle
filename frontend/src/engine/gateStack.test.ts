import { describe, expect, it } from 'vitest'
import {
  EMPTY_STACK,
  HANDOVER_MS,
  HOLD_MS,
  QUEUE_MAX,
  STALL_MS,
  stepStack,
  type StackState,
} from './gateStack'
import type { Exchange, Stage } from './store'

/** Only the fields the stack reads. The rest of an exchange never reaches it. */
function make(id: string, stage: Stage, stageAt: number): Exchange {
  return {
    id,
    requestId: `r-${id}`,
    openedAt: 0,
    stage,
    stageAt,
    clientLabel: 'Tablet · Studer',
    method: 'POST',
    scheme: 'https',
    host: 'api.helsana-app.ch',
    path: '/v1/claims',
    contentType: 'application/json',
    requestBody: '{}',
    entities: [],
    typeFrequencies: {},
    suppressed: 0,
    nearMisses: [],
  }
}

/** The store keeps exchanges newest first. */
function newestFirst(...exchanges: Exchange[]): Exchange[] {
  return [...exchanges].reverse()
}

describe('stepStack', () => {
  it('shows the first exchange as soon as it opens', () => {
    const step = stepStack(EMPTY_STACK, newestFirst(make('a', 'ingress', 0)), 100)

    expect(step.state.shown).toEqual(['a'])
    expect(step.leaving).toBeNull()
    expect(step.wakeAt).toBeNull()
  })

  it('keeps a delivered exchange while nothing is waiting, however long that is', () => {
    const one = newestFirst(make('a', 'done', 1000))
    const state: StackState = stepStack(EMPTY_STACK, one, 1000).state

    const step = stepStack(state, one, 1000 + HOLD_MS * 100)

    expect(step.state.shown).toEqual(['a'])
    expect(step.leaving).toBeNull()
    // Nothing to wake up for: the next request is what moves this on.
    expect(step.wakeAt).toBeNull()
  })

  it('hands a settled exchange on quickly once a live one is waiting', () => {
    // `a` is past its last transition; `b` has both of its still to come.
    const both = newestFirst(make('a', 'rehydrate', 900), make('b', 'ingress', 1200))
    let state = stepStack(EMPTY_STACK, both, 1200).state

    const early = stepStack(state, both, 1200 + HANDOVER_MS - 1)
    expect(early.state.shown).toEqual(['a', 'b'])
    expect(early.leaving).toBeNull()
    expect(early.wakeAt).toBe(1200 + HANDOVER_MS)

    state = early.state
    // Waiting any longer would put `b` at the gate with its values already
    // rewritten, which is the one thing the gate is there to show happening.
    const late = stepStack(state, both, 1200 + HANDOVER_MS)
    expect(late.state.shown).toEqual(['b', 'a'])
  })

  it('keeps a settled exchange the full hold when only settled ones wait', () => {
    const both = newestFirst(make('a', 'done', 1000), make('b', 'done', 1100))
    const state = stepStack(EMPTY_STACK, both, 1200).state

    const early = stepStack(state, both, 1200 + HOLD_MS - 1)
    expect(early.state.shown).toEqual(['a', 'b'])
    expect(early.wakeAt).toBe(1200 + HOLD_MS)

    const late = stepStack(state, both, 1200 + HOLD_MS)
    expect(late.state.shown).toEqual(['b'])
    expect(late.leaving).toBe('a')
  })

  it('retires a delivered card but only rotates one still in flight', () => {
    const settled = newestFirst(make('a', 'done', 1000), make('b', 'ingress', 1200))
    const done = stepStack(stepStack(EMPTY_STACK, settled, 1200).state, settled, 1200 + HANDOVER_MS)
    expect(done.state.shown).toEqual(['b'])
    expect(done.state.retired).toEqual(['a'])
    expect(done.leaving).toBe('a')

    const live = newestFirst(make('c', 'rehydrate', 1000), make('d', 'ingress', 1200))
    const kept = stepStack(stepStack(EMPTY_STACK, live, 1200).state, live, 1200 + HANDOVER_MS)
    expect(kept.state.shown).toEqual(['d', 'c'])
    // Retiring it would take its strip away and stop it coming back with its
    // numbers in; there is no exit animation for a card that only stepped back.
    expect(kept.state.retired).toEqual([])
    expect(kept.leaving).toBeNull()
  })

  it('keeps the front while the exchange is still moving through its stages', () => {
    let state = stepStack(EMPTY_STACK, newestFirst(make('a', 'ingress', 1000)), 1000).state

    // Each stage change resets the stall clock, so a slow but live exchange
    // keeps the gate however many stages it takes.
    for (const [stage, at] of [
      ['inspect', 5000],
      ['redact', 10000],
      ['egress', 15000],
    ] as const) {
      const both = newestFirst(make('a', stage, at), make('b', 'ingress', at))
      const step = stepStack(state, both, at + STALL_MS - 1)
      expect(step.state.shown).toEqual(['a', 'b'])
      expect(step.leaving).toBeNull()
      state = step.state
    }
  })

  it('sends a stalled exchange to the back of the queue rather than out', () => {
    const both = newestFirst(make('a', 'thinking', 1000), make('b', 'ingress', 1200))
    const state = stepStack(EMPTY_STACK, both, 1200).state
    expect(state.shown).toEqual(['a', 'b'])

    const early = stepStack(state, both, 1200 + STALL_MS - 1)
    expect(early.state.shown).toEqual(['a', 'b'])
    expect(early.wakeAt).toBe(1200 + STALL_MS)

    const late = stepStack(state, both, 1200 + STALL_MS)
    expect(late.state.shown).toEqual(['b', 'a'])
    expect(late.leaving).toBeNull()
  })

  it('gives a promoted card a full turn before it can stall in its own right', () => {
    const both = newestFirst(make('a', 'thinking', 1000), make('b', 'thinking', 1000))
    const state = stepStack(stepStack(EMPTY_STACK, both, 1200).state, both, 1200 + STALL_MS).state
    expect(state.shown).toEqual(['b', 'a'])

    // `b` has been stalled since 1000, but its turn only started at 1200 + STALL_MS.
    const early = stepStack(state, both, 1200 + STALL_MS + 1)
    expect(early.state.shown).toEqual(['b', 'a'])

    const late = stepStack(state, both, 1200 + STALL_MS * 2)
    expect(late.state.shown).toEqual(['a', 'b'])
  })

  it('does not queue a card that already had its turn', () => {
    const both = newestFirst(make('a', 'done', 1000), make('b', 'ingress', 1200))
    const state = stepStack(
      stepStack(EMPTY_STACK, both, 1200).state,
      both,
      1200 + HANDOVER_MS,
    ).state

    expect(stepStack(state, both, 1200 + HOLD_MS).state.shown).toEqual(['b'])
  })

  it('drops the oldest waiting cards when a burst piles up behind the front', () => {
    const many = newestFirst(
      make('a', 'done', 1000),
      make('b', 'ingress', 1100),
      make('c', 'ingress', 1200),
      make('d', 'ingress', 1300),
      make('e', 'ingress', 1400),
    )

    const step = stepStack(EMPTY_STACK, many, 1400)

    expect(step.state.shown).toEqual(['a', 'd', 'e'])
    expect(step.state.shown).toHaveLength(QUEUE_MAX + 1)
    // The front card was never off screen, so nothing animates out.
    expect(step.leaving).toBeNull()
  })

  it('forgets a card whose exchange the store has evicted', () => {
    const both = newestFirst(make('a', 'ingress', 1000), make('b', 'ingress', 1100))
    const state = stepStack(EMPTY_STACK, both, 1100).state

    const step = stepStack(state, newestFirst(make('b', 'ingress', 1100)), 1200)

    expect(step.state.shown).toEqual(['b'])
  })
})
