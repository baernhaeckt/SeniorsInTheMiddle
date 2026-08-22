import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { COPY } from '../copy'
import { edgeX, laneY } from '../engine/geometry'
import { type Stage } from '../engine/store'
import { useGateStack } from '../engine/useGateStack'
import { useStore } from '../engine/useStore'
import { cssVars } from '../ui/cssVars'
import { BREAKPOINT_NARROW } from '../ui/hooks'
import { Gate } from './Gate'
import { PacketLayer } from './PacketLayer'

interface Ripple {
  id: number
  top: number
  tone: 'warm' | 'cool'
}

/** An untreated request streaming straight through, never opened. */
interface Mote {
  id: number
  treatment: 'passthrough' | 'clean'
  offset: number
  durationMs: number
}

const MAX_MOTES = 26
const RIPPLE_MS = 1000

/**
 * Items that appear for a fixed time and then go. Each one owns its removal
 * timer, so a burst of new arrivals never cancels the cleanup of the last
 * batch. Everything pending is cleared when the component goes away.
 */
function useTransient<T extends { id: number }>(max?: number) {
  const [items, setItems] = useState<T[]>([])
  const timers = useRef(new Map<number, number>())

  useEffect(() => {
    const pending = timers.current
    return () => {
      for (const timer of pending.values()) window.clearTimeout(timer)
      pending.clear()
    }
  }, [])

  const add = (fresh: T[], lifetimeOf: (item: T) => number) => {
    if (fresh.length === 0) return
    setItems((current) => {
      const next = [...current, ...fresh]
      return max === undefined ? next : next.slice(-max)
    })
    for (const item of fresh) {
      const timer = window.setTimeout(() => {
        timers.current.delete(item.id)
        setItems((current) => current.filter((other) => other.id !== item.id))
      }, lifetimeOf(item))
      timers.current.set(item.id, timer)
    }
  }

  return [items, add] as const
}

export function FlowBand() {
  const exchanges = useStore((state) => state.exchanges)
  const traffic = useStore((state) => state.traffic)
  const proxy = useStore((state) => state.proxy)

  const band = useRef<HTMLDivElement>(null)
  const [size, setSize] = useState({ width: 0, height: 0 })
  const [ripples, addRipples] = useTransient<Ripple>()
  const [motes, addMotes] = useTransient<Mote>(MAX_MOTES)
  const seq = useRef(0)
  /** Last stage seen per exchange still on the band. Pruned with the store's list. */
  const seenStages = useRef(new Map<string, Stage>())
  const lastTrafficSeq = useRef(0)

  useLayoutEffect(() => {
    const element = band.current
    if (!element) return
    const observer = new ResizeObserver((entries) => {
      const entry = entries[0]
      if (entry) setSize({ width: entry.contentRect.width, height: entry.contentRect.height })
    })
    observer.observe(element)
    return () => {
      observer.disconnect()
    }
  }, [])

  // A packet reaching the wall sends a shockwave through it, both ways.
  useEffect(() => {
    if (size.height === 0) return
    const seen = seenStages.current
    const fresh: Ripple[] = []
    const alive = new Set<string>()

    for (const exchange of exchanges) {
      alive.add(exchange.id)
      const previous = seen.get(exchange.id)
      if (previous === exchange.stage) continue
      seen.set(exchange.id, exchange.stage)
      if (previous === undefined) continue

      if (exchange.stage === 'redact') {
        seq.current += 1
        fresh.push({ id: seq.current, top: laneY('request', size.height), tone: 'warm' })
      }
      if (exchange.stage === 'rehydrate') {
        seq.current += 1
        fresh.push({ id: seq.current, top: laneY('response', size.height), tone: 'cool' })
      }
    }
    for (const id of seen.keys()) if (!alive.has(id)) seen.delete(id)

    addRipples(fresh, () => RIPPLE_MS)
    // addRipples is stable for the component's lifetime.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [exchanges, size.height])

  // Untreated traffic crosses the middle without stopping. Most of the traffic
  // on a real line looks like this.
  useEffect(() => {
    const fresh: Mote[] = []
    let newest = lastTrafficSeq.current
    for (const entry of traffic) {
      if (entry.seq <= lastTrafficSeq.current) break
      newest = Math.max(newest, entry.seq)
      if (entry.treatment === 'treated') continue
      seq.current += 1
      fresh.push({
        id: seq.current,
        treatment: entry.treatment,
        offset: (seq.current % 7) * 7 - 21,
        durationMs: 1700 + (seq.current % 5) * 220,
      })
    }
    lastTrafficSeq.current = newest
    addMotes(fresh.reverse(), (mote) => mote.durationMs + 200)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [traffic])

  // The gate holds a finished exchange for a few seconds, so the nodes follow
  // its front card rather than the newest event: the middle of the band tells
  // one story at a time.
  const cards = useGateStack(exchanges)
  const active = cards.find((card) => !card.leaving)?.exchange ?? null
  const near = edgeX(size.width)
  const narrow = size.width < BREAKPOINT_NARROW

  return (
    <section
      className="band"
      ref={band}
      data-narrow={narrow}
      aria-label="Live traffic across the boundary"
    >
      <div className="band__zone band__zone--trusted" />
      <div className="band__zone band__zone--open" />

      <span className="u-label band__zonelabel band__zonelabel--trusted">Trusted side</span>
      <span className="u-label band__zonelabel band__zonelabel--open">Open internet</span>

      <div className="rail" style={cssVars({ '--y': `${laneY('request', size.height)}px` })}>
        <span className="rail__tag">Treated request →</span>
      </div>
      <div
        className="rail rail--return"
        style={cssVars({ '--y': `${laneY('response', size.height)}px` })}
      >
        <span className="rail__tag">← Restored response</span>
      </div>

      {size.width > 0 &&
        motes.map((mote) => (
          <span
            key={mote.id}
            className={`mote mote--${mote.treatment}`}
            style={cssVars({
              '--from': `${near}px`,
              '--to': `${size.width - near}px`,
              '--dy': `${mote.offset}px`,
              '--dur': `${mote.durationMs}ms`,
            })}
          />
        ))}

      <div className="wall" aria-hidden="true">
        <div className="wall__field" />
        <div className="wall__glow wall__glow--warm" />
        <div className="wall__glow wall__glow--cool" />
        <div className="wall__core" />
        <div className="wall__sweep" />
        {ripples.map((ripple) => (
          <span
            key={ripple.id}
            className={`wall__ripple wall__ripple--${ripple.tone}`}
            style={{ top: ripple.top }}
          />
        ))}
      </div>

      <div className="node node--trusted">
        <div className="node__ring">
          <ClientGlyph />
        </div>
        <div className="node__title">Client</div>
        <div className="node__meta">
          {active ? active.clientLabel : 'behind the proxy'}
          <br />
          {proxy?.region ?? COPY.defaultRegion}
        </div>
      </div>

      <div className="node node--open">
        <div className="node__ring">
          <OriginGlyph />
        </div>
        <div className="node__title">Destination</div>
        <div className="node__meta">{active ? active.host : 'any host'}</div>
      </div>

      <Gate cards={cards} proxy={proxy} />

      <PacketLayer exchanges={exchanges} width={size.width} height={size.height} />

      <ul className="legend">
        <li className="legend__item legend__item--passthrough">Passed untouched</li>
        <li className="legend__item legend__item--clean">Opened, nothing found</li>
        <li className="legend__item legend__item--treated">Treated</li>
      </ul>

      <p className="band__caption">
        Everything left of this line can identify a person. Nothing right of it can.
      </p>
    </section>
  )
}

function ClientGlyph() {
  return (
    <svg
      width="36"
      height="36"
      viewBox="0 0 28 28"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.3"
      aria-hidden="true"
    >
      <rect x="4.5" y="5" width="19" height="14" rx="1.6" />
      <path d="M10 22.5h8" strokeLinecap="round" />
      <path d="M14 19v3.5" strokeLinecap="round" />
    </svg>
  )
}

function OriginGlyph() {
  return (
    <svg
      width="36"
      height="36"
      viewBox="0 0 28 28"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.3"
      aria-hidden="true"
    >
      <circle cx="14" cy="14" r="10" />
      <ellipse cx="14" cy="14" rx="4.2" ry="10" />
      <path d="M4.4 11h19.2M4.4 17h19.2" strokeLinecap="round" />
    </svg>
  )
}
