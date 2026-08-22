import { memo, useEffect, useMemo, useRef, useState } from 'react'
import { easeInOut, originFor, slotFor, type PacketKind } from '../engine/geometry'
import { isTokenizedStage, stageViewOf } from '../engine/stageView'
import { store, type Exchange, type Stage } from '../engine/store'
import { excerptAround } from '../engine/text'
import { prefersReducedMotion } from '../ui/hooks'

const REQUEST_STAGES: ReadonlySet<Stage> = new Set([
  'ingress',
  'inspect',
  'redact',
  'egress',
  'thinking',
])
const RESPONSE_STAGES: ReadonlySet<Stage> = new Set(['return', 'rehydrate', 'deliver'])

/** Matches `easeInOut` in geometry.ts, for the browser to run the tween. */
const EASING = 'cubic-bezier(0.65, 0, 0.35, 1)'
const PACKET_WIDTH = 62
/** How long a delivered response lingers at the client before it leaves the band. */
const LINGER_MS = 900

interface PacketLayerProps {
  exchanges: Exchange[]
  width: number
  height: number
}

export function PacketLayer({ exchanges, width, height }: PacketLayerProps) {
  const packets = useMemo(() => {
    const list: { key: string; exchange: Exchange; kind: PacketKind }[] = []
    for (const exchange of exchanges) {
      if (REQUEST_STAGES.has(exchange.stage)) {
        list.push({ key: `${exchange.id}-req`, exchange, kind: 'request' })
      }
      if (RESPONSE_STAGES.has(exchange.stage)) {
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

const Packet = memo(function Packet({ exchange, kind, width, height }: PacketProps) {
  const node = useRef<HTMLDivElement>(null)
  // Where it first appears. The ref tracks where it is; the state is only read for the first paint.
  const [origin] = useState(() => originFor(kind, width, height))
  const position = useRef(origin)
  const animation = useRef<Animation | null>(null)
  const slot = slotFor(exchange.stage, kind, width, height)

  // Each new slot starts a tween from wherever the packet is right now, even
  // if the previous tween was still running.
  useEffect(() => {
    const element = node.current
    if (!element) return

    const from = { ...position.current }
    const to = { x: slot.x, y: slot.y }
    const duration = prefersReducedMotion() ? 1 : slot.travelMs

    const tween = element.animate([{ transform: translate(from) }, { transform: translate(to) }], {
      duration,
      easing: EASING,
      fill: 'forwards',
    })
    animation.current = tween
    tween.onfinish = () => {
      position.current = to
    }

    return () => {
      // Record where it got to, so the next tween can pick up from there.
      const progress = tween.effect?.getComputedTiming().progress ?? 1
      const eased = easeInOut(progress)
      position.current = {
        x: from.x + (to.x - from.x) * eased,
        y: from.y + (to.y - from.y) * eased,
      }
      tween.cancel()
      element.style.transform = translate(position.current)
    }
  }, [slot.x, slot.y, slot.travelMs])

  // Two stages end when the packet arrives, not when the proxy says so:
  // a dispatched request is "thinking" once it has left the gate, and a
  // delivered response leaves the band once it has reached the client.
  useEffect(() => {
    const settles =
      (kind === 'request' && exchange.stage === 'egress') ||
      (kind === 'response' && exchange.stage === 'deliver')
    if (!settles) return
    const wait = slot.travelMs + (exchange.stage === 'deliver' ? LINGER_MS : 0)
    const timer = window.setTimeout(() => {
      store.settle(exchange.id)
    }, wait)
    return () => {
      window.clearTimeout(timer)
    }
  }, [kind, exchange.stage, exchange.id, slot.travelMs])

  const view = viewOf(exchange, kind)

  return (
    <div
      ref={node}
      className={[
        'pk',
        slot.tone === 'warm' ? 'pk--warm' : 'pk--cool',
        exchange.stage === 'inspect' && kind === 'request' ? 'pk--scanned' : '',
        slot.docked ? 'pk--docked' : '',
      ]
        .filter(Boolean)
        .join(' ')}
      style={{ transform: translate(origin) }}
      aria-hidden="true"
    >
      <div className="pk__top">
        <span className="pk__who">{kind === 'request' ? exchange.clientLabel : exchange.host}</span>
        <span className={`pk__count${view.badgeTone ? ` pk__count--${view.badgeTone}` : ''}`}>
          {view.badge}
        </span>
      </div>
      <div className="pk__text">
        {view.excerpt.before}
        {view.excerpt.focus && (
          <span className={view.tokenized ? 'tm__token' : 'tm__real'}>{view.excerpt.focus}</span>
        )}
        {view.excerpt.after}
      </div>
    </div>
  )
})

function translate({ x, y }: { x: number; y: number }): string {
  return `translate3d(${x}px, ${y}px, 0)`
}

/** What this packet is carrying at this exact point of the trip. */
function viewOf(exchange: Exchange, kind: PacketKind) {
  const count = exchange.entities.length
  const tokenized = isTokenizedStage(exchange.stage)
  const body = stageViewOf(exchange)
  const excerpt = excerptAround(body.text, body.focus, PACKET_WIDTH)

  if (kind === 'request') {
    return {
      excerpt,
      badge: tokenized ? `${count} held` : count > 0 ? `${count} PII` : 'scanning',
      tokenized,
      badgeTone: tokenized ? 'clear' : null,
    }
  }

  const restored = exchange.stage === 'deliver'
  return {
    excerpt,
    badge: restored ? `${count} restored` : 'tokenized',
    tokenized: !restored,
    badgeTone: restored ? 'home' : 'clear',
  }
}
