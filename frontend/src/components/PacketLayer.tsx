import { useEffect, useMemo, useRef } from 'react'
import type { Entity } from '../protocol/types'
import { easeInOut, originFor, slotFor, type PacketKind } from '../engine/geometry'
import { excerptAround } from '../engine/text'
import { retireExchange, type Exchange, type Stage } from '../engine/store'

const REQUEST_STAGES: Stage[] = ['ingress', 'inspect', 'redact', 'egress', 'thinking']
const RESPONSE_STAGES: Stage[] = ['return', 'rehydrate', 'deliver']

interface PacketLayerProps {
  exchanges: Exchange[]
  width: number
  height: number
}

export function PacketLayer({ exchanges, width, height }: PacketLayerProps) {
  const packets = useMemo(() => {
    const list: { key: string; exchange: Exchange; kind: PacketKind }[] = []
    for (const exchange of exchanges) {
      if (REQUEST_STAGES.includes(exchange.stage)) {
        list.push({ key: `${exchange.id}-req`, exchange, kind: 'request' })
      }
      if (RESPONSE_STAGES.includes(exchange.stage)) {
        list.push({ key: `${exchange.id}-res`, exchange, kind: 'response' })
      }
    }
    return list
  }, [exchanges])

  if (width === 0) return null

  return (
    <>
      {packets.map(({ key, exchange, kind }) => (
        <Packet key={key} exchange={exchange} kind={kind} width={width} height={height} />
      ))}
    </>
  )
}

interface PacketProps {
  exchange: Exchange
  kind: PacketKind
  width: number
  height: number
}

function Packet({ exchange, kind, width, height }: PacketProps) {
  const node = useRef<HTMLDivElement>(null)
  const position = useRef(originFor(kind, width, height))
  const slot = slotFor(exchange.stage, kind, width, height)

  useEffect(() => {
    const element = node.current
    if (!element) return

    const from = { ...position.current }
    const to = { x: slot.x, y: slot.y }
    const started = performance.now()
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    const duration = reduced ? 1 : slot.travelMs
    let raf = 0

    const tick = (now: number) => {
      const progress = Math.min(1, (now - started) / duration)
      const eased = easeInOut(progress)
      position.current = {
        x: from.x + (to.x - from.x) * eased,
        y: from.y + (to.y - from.y) * eased,
      }
      element.style.transform = `translate3d(${position.current.x}px, ${position.current.y}px, 0)`
      if (progress < 1) raf = requestAnimationFrame(tick)
    }

    raf = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(raf)
  }, [slot.x, slot.y, slot.travelMs])

  // A delivered response leaves the band once it has reached the client.
  useEffect(() => {
    if (kind !== 'response' || exchange.stage !== 'deliver') return
    const timer = window.setTimeout(() => retireExchange(exchange.id), slot.travelMs + 900)
    return () => window.clearTimeout(timer)
  }, [kind, exchange.stage, exchange.id, slot.travelMs])

  const view = viewOf(exchange, kind)
  const initial = position.current

  return (
    <div
      ref={node}
      className={[
        'pk',
        slot.tone === 'warm' ? 'pk--warm' : 'pk--cool',
        exchange.stage === 'inspect' && kind === 'request' ? 'pk--scanned' : '',
        slot.docked ? 'pk--docked' : '',
      ].join(' ')}
      style={{ transform: `translate3d(${initial.x}px, ${initial.y}px, 0)` }}
      aria-hidden="true"
    >
      <div className="pk__top">
        <span className="pk__who">
          {kind === 'request' ? exchange.clientLabel : exchange.host}
        </span>
        <span className={`pk__count${view.badgeTone ? ` pk__count--${view.badgeTone}` : ''}`}>
          {view.badge}
        </span>
      </div>
      <div className="pk__text">
        {view.excerpt.before}
        {view.excerpt.focus && (
          <span className={view.clear ? 'tm__token' : 'tm__real'}>{view.excerpt.focus}</span>
        )}
        {view.excerpt.after}
      </div>
    </div>
  )
}

/** What this packet is carrying at this exact point of the trip. */
function viewOf(exchange: Exchange, kind: PacketKind) {
  const first: Entity | undefined = exchange.entities[0]
  const count = exchange.entities.length

  if (kind === 'request') {
    const tokenized = exchange.stage === 'egress' || exchange.stage === 'thinking'
    const text = tokenized ? (exchange.redactedRequestBody ?? exchange.requestBody) : exchange.requestBody
    const focus = tokenized ? (first?.token ?? '') : (first?.value ?? '')
    return {
      excerpt: excerptAround(text, focus, 62),
      badge: tokenized ? `${count} held` : count > 0 ? `${count} PII` : 'scanning',
      clear: tokenized,
      badgeTone: tokenized ? 'clear' : null,
    }
  }

  const restored = exchange.stage === 'deliver'
  const text = restored ? (exchange.responseBody ?? '') : (exchange.tokenizedResponseBody ?? '')
  const focus = restored ? (first?.value ?? '') : (first?.token ?? '')
  return {
    excerpt: excerptAround(text, focus, 62),
    badge: restored ? `${count} restored` : 'tokenized',
    clear: !restored,
    badgeTone: restored ? 'home' : 'clear',
  }
}
