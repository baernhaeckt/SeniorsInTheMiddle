import { isSpentStage } from './stageView'
import type { Exchange } from './store'

/**
 * How long a spent exchange keeps the gate when nothing else has anything to
 * show.
 *
 * The card stays at least this long after its payload settled, and beyond it
 * until something is waiting: a wall display is watched between requests more
 * than during them, and an empty box in the middle of the screen says nothing
 * about what the proxy is for.
 */
export const HOLD_MS = 5000

/**
 * How long a spent exchange keeps the gate once a live one is waiting.
 *
 * The hold above is for a quiet boundary. With something in flight behind the
 * card, the trade reverses: the card in front has finished changing and the one
 * behind it has not started. Long enough for the last churn to land and be
 * read; short enough that the next request reaches the gate before its own
 * values turn into tokens, which is the moment the whole display exists for.
 *
 * Waiting out anything longer put every card at the gate with its two
 * transitions already behind it, which is a still picture of a finished
 * request — accurate, and no reason to look up.
 */
export const HANDOVER_MS = 1200

/**
 * How long the gate waits on a card that has stopped moving.
 *
 * A request can sit in one stage for as long as its timeout allows — a slow
 * destination, a reply that never comes — and while it does, the card in the
 * middle of the screen is a still picture and every treated request behind it
 * is waiting its turn. Past this, the card gives up the front.
 *
 * The clock runs from whichever came later, its last stage change or its
 * arrival at the front, so a card that is moving normally is never cut off and
 * one promoted from a long wait still gets a full turn.
 */
export const STALL_MS = 6000

/** Cards waiting behind the front one. Older ones never get their turn. */
export const QUEUE_MAX = 2

/** Ids remembered after their turn, so a retired card is never queued again. */
const RETIRED_MEMORY = 40

export interface StackState {
  /** Exchange ids, front first. The first one is what the gate narrates. */
  shown: string[]
  retired: string[]
  /** When the front card took the gate, for the stall clock. */
  frontSince: number
  /** When the front card last had something to show, or 0 while it still has. */
  spentSince: number
}

export const EMPTY_STACK: StackState = { shown: [], retired: [], frontSince: 0, spentSince: 0 }

export interface StackStep {
  state: StackState
  /** The card that just lost the front of the gate, for its exit animation. */
  leaving: string | null
  /** When the front card gives way, or null when nothing is waiting for it. */
  wakeAt: number | null
}

interface Clocks {
  frontSince: number
  spentSince: number
  /** Whether anything behind the front still has a transition to show. */
  telling: boolean
}

/** When the front card is due to hand the gate on, whichever way it goes. */
function yieldsAt(front: Exchange, clocks: Clocks): number {
  if (isSpentStage(front.stage)) {
    return clocks.spentSince + (clocks.telling ? HANDOVER_MS : HOLD_MS)
  }
  return Math.max(front.stageAt, clocks.frontSince) + STALL_MS
}

/**
 * Advance the stack to what it should look like at `now`.
 *
 * Pure, so the timing rules can be tested against a clock rather than a timer.
 * A card leaves the front two ways: delivered and read, in which case it is
 * done and retires; or still in flight, in which case it goes to the back of
 * the queue rather than out. Nothing is lost that way — it keeps its strip
 * under the card, and it comes back to the gate settled, with its numbers in.
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
  const kept = shown[0] === state.shown[0]
  let frontSince = kept ? state.frontSince : now
  let spentSince = kept ? state.spentSince : 0

  /** Brings both clocks up to date for whatever is at the front right now. */
  const clocks = (front: Exchange): Clocks => {
    // A card promoted with its payload already settled starts its beat now.
    spentSince = isSpentStage(front.stage) ? spentSince || now : 0
    return {
      frontSince,
      spentSince,
      telling: shown.slice(1).some((id) => {
        const stage = byId.get(id)?.stage
        return stage !== undefined && !isSpentStage(stage)
      }),
    }
  }

  while (shown.length > 1) {
    const front = byId.get(shown[0] ?? '')
    if (!front) break
    if (now < yieldsAt(front, clocks(front))) break

    const id = shown.shift()
    if (!id) break
    frontSince = now
    spentSince = 0

    if (front.stage === 'done') {
      gone.push(id)
      leaving ??= id
      continue
    }

    // Rotated rather than retired. One per step: the card promoted behind it
    // has only just arrived at the front and its turn starts now.
    shown.push(id)
    break
  }

  // A burst arriving while one card holds the gate would otherwise queue
  // minutes of stale traffic. The newest waiting cards win, and a card that
  // just rotated counts as the newest — it is at the back of the queue.
  if (shown.length > QUEUE_MAX + 1) {
    gone.push(...shown.splice(1, shown.length - QUEUE_MAX - 1))
  }

  const front = byId.get(shown[0] ?? '')
  const wakeAt = front && shown.length > 1 ? yieldsAt(front, clocks(front)) : null

  return {
    state: {
      shown,
      retired: [...gone, ...state.retired].slice(0, RETIRED_MEMORY),
      frontSince,
      spentSince,
    },
    leaving,
    wakeAt,
  }
}
