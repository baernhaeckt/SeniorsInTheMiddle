import type { Exchange } from './store'

/**
 * How long a delivered exchange keeps the gate.
 *
 * The card stays at least this long after it reached the client, and beyond it
 * until something newer is waiting: a wall display is watched between requests
 * more than during them, and an empty box in the middle of the screen says
 * nothing about what the proxy is for.
 */
export const HOLD_MS = 5000

/** Cards waiting behind the front one. Older ones never get their turn. */
export const QUEUE_MAX = 2

/** Ids remembered after their turn, so a retired card is never queued again. */
const RETIRED_MEMORY = 40

export interface StackState {
  /** Exchange ids, front first. The first one is what the gate narrates. */
  shown: string[]
  retired: string[]
}

export const EMPTY_STACK: StackState = { shown: [], retired: [] }

export interface StackStep {
  state: StackState
  /** The card that just lost the front of the gate, for its exit animation. */
  leaving: string | null
  /** When the front card's hold runs out, or null when nothing is pending. */
  wakeAt: number | null
}

/**
 * Advance the stack to what it should look like at `now`.
 *
 * Pure, so the hold rule can be tested against a clock rather than a timer:
 * the front card gives way only once it has been delivered, its hold has run
 * out, and there is something waiting to replace it — whichever of those two
 * takes longer.
 */
export function stepStack(state: StackState, exchanges: Exchange[], now: number): StackStep {
  const byId = new Map(exchanges.map((exchange) => [exchange.id, exchange]))

  // An exchange the store has evicted takes its card with it.
  const shown = state.shown.filter((id) => byId.has(id))
  const known = new Set([...shown, ...state.retired])

  // `exchanges` is newest first; the queue runs in arrival order.
  for (let index = exchanges.length - 1; index >= 0; index -= 1) {
    const exchange = exchanges[index]
    if (exchange && !known.has(exchange.id)) shown.push(exchange.id)
  }

  const gone: string[] = []
  // Only the first card to give way was on screen at full size; anything behind
  // it is promoted and retired in the same breath, and never seen.
  let leaving: string | null = null
  let wakeAt: number | null = null
  while (shown.length > 1) {
    const front = byId.get(shown[0] ?? '')
    if (front?.stage !== 'done') break
    const until = front.stageAt + HOLD_MS
    if (now < until) {
      wakeAt = until
      break
    }
    const id = shown.shift()
    if (!id) break
    gone.push(id)
    leaving ??= id
  }

  // A burst arriving while one card holds the gate would otherwise queue
  // minutes of stale traffic. The newest waiting cards win.
  if (shown.length > QUEUE_MAX + 1) {
    gone.push(...shown.splice(1, shown.length - QUEUE_MAX - 1))
  }

  return {
    state: { shown, retired: [...gone, ...state.retired].slice(0, RETIRED_MEMORY) },
    leaving,
    wakeAt,
  }
}
