import { describe, expect, it } from 'vitest'
import { EMPTY_STACK, HOLD_MS, QUEUE_MAX, stepStack, type StackState } from './gateStack'
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
    let state: StackState = stepStack(EMPTY_STACK, one, 1000).state

    const step = stepStack(state, one, 1000 + HOLD_MS * 100)

    expect(step.state.shown).toEqual(['a'])
    expect(step.leaving).toBeNull()
    // Nothing to wake up for: the next request is what moves this on.
    expect(step.wakeAt).toBeNull()
    state = step.state
  })

  it('holds a delivered exchange for five seconds even once the next one is in', () => {
    const both = newestFirst(make('a', 'done', 1000), make('b', 'ingress', 1200))
    let state = stepStack(EMPTY_STACK, both, 1200).state

    const early = stepStack(state, both, 1000 + HOLD_MS - 1)
    expect(early.state.shown).toEqual(['a', 'b'])
    expect(early.leaving).toBeNull()
    expect(early.wakeAt).toBe(1000 + HOLD_MS)

    state = early.state
    const late = stepStack(state, both, 1000 + HOLD_MS)
    expect(late.state.shown).toEqual(['b'])
    expect(late.leaving).toBe('a')
  })

  it('never gives up the front while the exchange is still in flight', () => {
    const both = newestFirst(make('a', 'thinking', 1000), make('b', 'ingress', 1200))
    const state = stepStack(EMPTY_STACK, both, 1200).state

    const step = stepStack(state, both, 1200 + HOLD_MS * 10)

    expect(step.state.shown).toEqual(['a', 'b'])
    expect(step.leaving).toBeNull()
  })

  it('does not queue a card that already had its turn', () => {
    const both = newestFirst(make('a', 'done', 1000), make('b', 'ingress', 1200))
    const state = stepStack(stepStack(EMPTY_STACK, both, 1200).state, both, 1000 + HOLD_MS).state

    expect(stepStack(state, both, 1000 + HOLD_MS + 1).state.shown).toEqual(['b'])
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
