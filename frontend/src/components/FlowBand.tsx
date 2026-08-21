import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Gate } from './Gate'
import { PacketLayer } from './PacketLayer'
import { edgeX, laneY } from '../engine/geometry'
import type { AppState, Exchange, Stage } from '../engine/store'

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

let rippleSeq = 0
let moteSeq = 0

const MAX_MOTES = 26

export function FlowBand({ state }: { state: AppState }) {
  const band = useRef<HTMLDivElement>(null)
  const [size, setSize] = useState({ width: 0, height: 0 })
  const [ripples, setRipples] = useState<Ripple[]>([])
  const [motes, setMotes] = useState<Mote[]>([])
  const seenStages = useRef(new Map<string, Stage>())
  const seenRequests = useRef(new Set<string>())

  useLayoutEffect(() => {
    const element = band.current
    if (!element) return
    const observer = new ResizeObserver(([entry]) => {
      setSize({ width: entry.contentRect.width, height: entry.contentRect.height })
    })
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  // A packet reaching the wall sends a shockwave through it, both ways.
  useEffect(() => {
    if (size.height === 0) return
    const fresh: Ripple[] = []

    for (const exchange of state.exchanges) {
      const previous = seenStages.current.get(exchange.id)
      if (previous === exchange.stage) continue
      seenStages.current.set(exchange.id, exchange.stage)
      if (previous === undefined) continue

      if (exchange.stage === 'redact') {
        rippleSeq += 1
        fresh.push({ id: rippleSeq, top: laneY('request', size.height), tone: 'warm' })
      }
      if (exchange.stage === 'rehydrate') {
        rippleSeq += 1
        fresh.push({ id: rippleSeq, top: laneY('response', size.height), tone: 'cool' })
      }
    }

    if (fresh.length === 0) return
    setRipples((current) => [...current, ...fresh])
    const timer = window.setTimeout(() => {
      const ids = new Set(fresh.map((item) => item.id))
      setRipples((current) => current.filter((item) => !ids.has(item.id)))
    }, 1000)
    return () => window.clearTimeout(timer)
  }, [state.exchanges, size.height])

  // Untreated traffic crosses the middle without stopping. Most of the traffic
  // on a real line looks like this.
  useEffect(() => {
    const fresh: Mote[] = []
    for (const entry of state.traffic) {
      if (seenRequests.current.has(entry.requestId)) continue
      seenRequests.current.add(entry.requestId)
      if (entry.treatment === 'treated') continue
      moteSeq += 1
      fresh.push({
        id: moteSeq,
        treatment: entry.treatment,
        offset: (moteSeq % 7) * 7 - 21,
        durationMs: 1700 + (moteSeq % 5) * 220,
      })
    }
    if (fresh.length === 0) return

    setMotes((current) => [...current, ...fresh].slice(-MAX_MOTES))
    const longest = Math.max(...fresh.map((mote) => mote.durationMs))
    const timer = window.setTimeout(() => {
      const ids = new Set(fresh.map((mote) => mote.id))
      setMotes((current) => current.filter((mote) => !ids.has(mote.id)))
    }, longest + 200)
    return () => window.clearTimeout(timer)
  }, [state.traffic])

  const active = activeExchange(state.exchanges)
  const near = edgeX(size.width)

  return (
    <section className="band" ref={band} aria-label="Live traffic across the boundary">
      <div className="band__zone band__zone--trusted" />
      <div className="band__zone band__zone--open" />

      <span className="u-label band__zonelabel band__zonelabel--trusted">Trusted side</span>
      <span className="u-label band__zonelabel band__zonelabel--open">Open internet</span>

      <div
        className="rail"
        style={{ '--y': `${laneY('request', size.height)}px` } as React.CSSProperties}
      >
        <span className="rail__tag">Treated request →</span>
      </div>
      <div
        className="rail rail--return"
        style={{ '--y': `${laneY('response', size.height)}px` } as React.CSSProperties}
      >
        <span className="rail__tag">← Restored response</span>
      </div>

      {size.width > 0 &&
        motes.map((mote) => (
          <span
            key={mote.id}
            className={`mote mote--${mote.treatment}`}
            style={
              {
                '--from': `${near}px`,
                '--to': `${size.width - near}px`,
                '--dy': `${mote.offset}px`,
                '--dur': `${mote.durationMs}ms`,
              } as React.CSSProperties
            }
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

      <div className="node node--trusted" style={{ left: `${size.width < 760 ? 16 : 11.5}%` }}>
        <div className="node__ring">
          <ClientGlyph />
        </div>
        <div className="node__title">Client</div>
        <div className="node__meta">
          {active ? active.clientLabel : 'behind the proxy'}
          <br />
          {state.proxy?.region ?? 'Bern'}
        </div>
      </div>

      <div className="node node--open" style={{ left: `${size.width < 760 ? 84 : 88.5}%` }}>
        <div className="node__ring">
          <OriginGlyph />
        </div>
        <div className="node__title">Destination</div>
        <div className="node__meta">
          {active ? active.host : 'any host'}
          <br />
          sees tokens only
        </div>
      </div>

      <Gate active={active} proxy={state.proxy} />

      <PacketLayer exchanges={state.exchanges} width={size.width} height={size.height} />

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

/** The newest treated exchange still in motion. Untreated traffic never lands here. */
function activeExchange(exchanges: Exchange[]): Exchange | null {
  return exchanges.find((exchange) => exchange.stage !== 'done') ?? null
}

function ClientGlyph() {
  return (
    <svg width="36" height="36" viewBox="0 0 28 28" fill="none" stroke="currentColor" strokeWidth="1.3">
      <rect x="4.5" y="5" width="19" height="14" rx="1.6" />
      <path d="M10 22.5h8" strokeLinecap="round" />
      <path d="M14 19v3.5" strokeLinecap="round" />
    </svg>
  )
}

function OriginGlyph() {
  return (
    <svg width="36" height="36" viewBox="0 0 28 28" fill="none" stroke="currentColor" strokeWidth="1.3">
      <circle cx="14" cy="14" r="10" />
      <ellipse cx="14" cy="14" rx="4.2" ry="10" />
      <path d="M4.4 11h19.2M4.4 17h19.2" strokeLinecap="round" />
    </svg>
  )
}
