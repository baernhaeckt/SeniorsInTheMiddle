import { useEffect, useRef, useState } from 'react'
import { EMPTY_STACK, stepStack, type StackState } from './gateStack'
import type { Exchange } from './store'

/** How long a retired card stays mounted for its exit animation. Mirrors `cardOut` in band.css. */
const LEAVE_MS = 460

export interface GateCard {
  exchange: Exchange
  /** On its way out: still mounted so the exit animation can finish. */
  leaving: boolean
}

/**
 * Which exchanges the gate is showing, front first.
 *
 * The hold rule lives in `stepStack`; this only supplies it with a clock. A
 * timer wakes the stack when the front card's hold runs out, because nothing
 * else would: the store has no reason to emit an event five seconds after the
 * last one.
 */
export function useGateStack(exchanges: Exchange[]): GateCard[] {
  const stack = useRef<StackState>(EMPTY_STACK)
  const leaving = useRef(new Map<string, Exchange>())
  const exits = useRef(new Map<string, number>())
  const wake = useRef(0)
  const latest = useRef(exchanges)
  const [cards, setCards] = useState<GateCard[]>([])

  useEffect(() => {
    // Held so the wake-up timer, which fires between renders, reads the list as
    // it stands rather than the one it closed over.
    latest.current = exchanges

    const build = (): GateCard[] => [
      ...[...leaving.current.values()].map((exchange) => ({ exchange, leaving: true })),
      ...stack.current.shown.flatMap((id) => {
        const exchange = latest.current.find((item) => item.id === id)
        return exchange ? [{ exchange, leaving: false }] : []
      }),
    ]

    const run = () => {
      const step = stepStack(stack.current, latest.current, Date.now())
      stack.current = step.state

      const left = step.leaving && latest.current.find((item) => item.id === step.leaving)
      if (left) {
        leaving.current.set(left.id, left)
        const timer = window.setTimeout(() => {
          exits.current.delete(left.id)
          leaving.current.delete(left.id)
          setCards(build())
        }, LEAVE_MS)
        exits.current.set(left.id, timer)
      }

      window.clearTimeout(wake.current)
      if (step.wakeAt !== null) {
        wake.current = window.setTimeout(run, Math.max(0, step.wakeAt - Date.now()) + 30)
      }
      setCards(build())
    }

    run()
  }, [exchanges])

  useEffect(() => {
    const pending = exits.current
    return () => {
      window.clearTimeout(wake.current)
      for (const timer of pending.values()) window.clearTimeout(timer)
      pending.clear()
    }
  }, [])

  return cards
}
