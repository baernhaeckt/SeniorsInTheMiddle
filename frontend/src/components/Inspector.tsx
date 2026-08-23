import {
  memo,
  useRef,
  useState,
  type Dispatch,
  type KeyboardEvent,
  type PointerEvent,
  type RefObject,
  type SetStateAction,
} from 'react'
import { shownExchange, store, type Exchange } from '../engine/store'
import { prettyBody, splitByOffsets, splitByValues } from '../engine/text'
import { useStore } from '../engine/useStore'
import type { Entity } from '../protocol/types'
import { cssVars } from '../ui/cssVars'
import { useInspectorFocus } from '../ui/focus'
import { Insights } from './Insights'
import { chipTitle, isPhi } from '../engine/entityFacts'

/** How far the wipe can travel, so neither side can be closed off entirely. */
const MIN_AT = 0.04
const MAX_AT = 0.96

/** A nudge from the arrow keys. */
const STEP = 0.04

/** Pointer travel past which a press counts as a drag rather than a click. */
const DRAG_SLOP = 4

/**
 * Where the wipe opens: the middle, which for most payloads is past the end of
 * the lines and so shows the client's side whole. Dragging it back across the
 * values is what puts a name against its token; the far side going blank on the
 * way is the redacted copy being the shorter of the two, which is the point.
 */
const OPENS_AT = 0.5

export function Inspector() {
  const exchanges = useStore((state) => state.exchanges)
  const pinnedId = useStore((state) => state.pinnedId)
  const hoveredToken = useStore((state) => state.hoveredToken)
  const policy = useStore((state) => state.policy)
  const shown = shownExchange(exchanges, pinnedId)
  const following = pinnedId === null

  // Where the wipe stands, 0 at the left edge. One position for both rows: the
  // question it answers -- what did this request look like on either side of
  // the boundary -- is the same question for the request and for the reply.
  const [at, setAt] = useState(OPENS_AT)
  const rule = useRef<HTMLDivElement>(null)
  const [focused, toggleFocus] = useInspectorFocus()

  return (
    <section className="panel" aria-label="Payload inspector">
      <div className="panel__head">
        <span className="u-label">Payload inspector</span>
        <div className="hist">
          {shown && (
            <span className="panel__note">
              {shown.method} {shown.host}
              {shown.path}
            </span>
          )}
          <button
            type="button"
            className="hist__btn"
            data-active={following}
            onClick={() => {
              store.pin(null)
            }}
          >
            {following ? 'following live' : 'follow live'}
          </button>
          <button
            type="button"
            className="hist__btn"
            data-active={focused}
            aria-pressed={focused}
            title={focused ? 'Back to the even split' : 'Give the inspector most of the screen'}
            onClick={toggleFocus}
          >
            {focused ? 'shrink' : 'expand'}
          </button>
        </div>
      </div>

      <div className="panel__body">
        <div className="matrix" style={cssVars({ '--at': String(at) })}>
          <div className="matrix__head">
            <span className="u-label u-label--warm">What the client sent</span>
            <span className="panel__note">{shown?.contentType ?? ''}</span>
            <span className="u-label u-label--cool">What the destination saw</span>
          </div>

          <div className="matrix__body">
            <Pair
              label="Outbound"
              exchange={shown}
              hoveredToken={hoveredToken}
              sent="requestBody"
              saw="redactedRequestBody"
            />
            <Pair
              label="Inbound"
              exchange={shown}
              hoveredToken={hoveredToken}
              sent="responseBody"
              saw="tokenizedResponseBody"
            />

            {/* Last, so it draws over both rows as one ruler. */}
            <div className="matrix__rule" ref={rule}>
              <SplitBar at={at} onChange={setAt} area={rule} />
            </div>
          </div>
        </div>
      </div>

      <Insights exchange={shown} policy={policy} />
    </section>
  )
}

type BodyField = 'requestBody' | 'redactedRequestBody' | 'responseBody' | 'tokenizedResponseBody'

interface PairProps {
  label: string
  exchange: Exchange | null
  hoveredToken: string | null
  sent: BodyField
  saw: BodyField
}

/**
 * The two readings of one body, stacked rather than side by side.
 *
 * Two columns halved the width a payload had to lay itself out in, which is the
 * one thing a JSON body cannot spare — and it left the reader comparing two
 * blocks of text a screen apart. Over each other, every line is in the same
 * place in both, so the wipe swaps a name for its token where the name stood.
 *
 * One scroller holds both, so they cannot drift out of step.
 */
function Pair({ label, exchange, hoveredToken, sent, saw }: PairProps) {
  return (
    <div className="matrix__row">
      <div className="matrix__rowhead">
        <span>{label}</span>
      </div>
      <div className="split">
        <Cell exchange={exchange} hoveredToken={hoveredToken} field={sent} tone="real" />
        <Cell exchange={exchange} hoveredToken={hoveredToken} field={saw} tone="token" />
      </div>
    </div>
  )
}

interface SplitBarProps {
  at: number
  onChange: Dispatch<SetStateAction<number>>
  /** The band the wipe travels across, for turning a pointer into a position. */
  area: RefObject<HTMLDivElement>
}

function clamp(at: number): number {
  return Math.min(MAX_AT, Math.max(MIN_AT, at))
}

/**
 * The ruler between the two readings. Drag it to wipe; press it to jump to
 * whichever side is currently the more hidden of the two.
 */
function SplitBar({ at, onChange, area }: SplitBarProps) {
  const from = useRef(0)
  const dragged = useRef(false)

  const positionFrom = (clientX: number) => {
    const box = area.current?.getBoundingClientRect()
    if (!box || box.width === 0) return null
    return clamp((clientX - box.left) / box.width)
  }

  const onPointerDown = (event: PointerEvent<HTMLButtonElement>) => {
    event.currentTarget.setPointerCapture(event.pointerId)
    from.current = event.clientX
    dragged.current = false
  }

  const onPointerMove = (event: PointerEvent<HTMLButtonElement>) => {
    if (!event.currentTarget.hasPointerCapture(event.pointerId)) return
    // A press wobbles by a pixel or two; that is not someone dragging.
    if (!dragged.current && Math.abs(event.clientX - from.current) < DRAG_SLOP) return
    dragged.current = true
    const next = positionFrom(event.clientX)
    if (next !== null) onChange(next)
  }

  const onKeyDown = (event: KeyboardEvent<HTMLButtonElement>) => {
    // From the position at the time of the press, not at the time of the last
    // render: a held arrow key repeats faster than React re-renders.
    const moves: Record<string, (from: number) => number> = {
      ArrowLeft: (from) => from - STEP,
      ArrowRight: (from) => from + STEP,
      Home: () => MIN_AT,
      End: () => MAX_AT,
    }
    const move = moves[event.key]
    if (!move) return
    event.preventDefault()
    onChange((from) => clamp(move(from)))
  }

  return (
    <button
      type="button"
      className="splitbar"
      aria-label={`Reveal what the client sent or what the destination saw. ${Math.round(
        at * 100,
      )}% sent.`}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onKeyDown={onKeyDown}
      onClick={() => {
        // A drag ends in a click event too; only a press without one counts.
        if (dragged.current) return
        // From the middle, the first press shows the destination's copy: the
        // client's is what the box is already showing.
        onChange((from) => (from >= 0.5 ? MIN_AT : MAX_AT))
      }}
    >
      <span className="splitbar__grip" aria-hidden="true">
        <svg width="13" height="9" viewBox="0 0 13 9" fill="none">
          <path
            d="M4.4 1.4 1.4 4.5l3 3.1M8.6 1.4l3 3.1-3 3.1"
            stroke="currentColor"
            strokeWidth="1.2"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      </span>
    </button>
  )
}

interface CellProps {
  exchange: Exchange | null
  hoveredToken: string | null
  field: BodyField
  tone: 'real' | 'token'
}

const WAITING: Record<BodyField, string> = {
  requestBody: 'waiting for a request worth reading',
  redactedRequestBody: 'not rewritten yet',
  responseBody: 'nothing delivered back yet',
  tokenizedResponseBody: 'destination has not answered yet',
}

const Cell = memo(function Cell({ exchange, hoveredToken, field, tone }: CellProps) {
  const raw = exchange?.[field]
  const className = `split__pane split__pane--${tone === 'real' ? 'sent' : 'saw'}`

  if (!exchange || !raw) {
    return (
      <div className={className}>
        <span className="matrix__empty">{WAITING[field]}</span>
      </div>
    )
  }

  const text = prettyBody(raw, exchange.contentType)
  // The raw request is the one body the offsets describe; the others are searched.
  const runs =
    field === 'requestBody' && text === raw
      ? splitByOffsets(text, exchange.entities)
      : splitByValues(text, exchange.entities, tone === 'token')

  return (
    <div className={className}>
      {runs.map((run, index) =>
        run.entity ? (
          <Chip
            key={index}
            entity={run.entity}
            text={run.text}
            tone={tone}
            hot={hoveredToken === run.entity.token}
          />
        ) : (
          <span key={index}>{run.text}</span>
        ),
      )}
    </div>
  )
})

interface ChipProps {
  entity: Entity
  text: string
  tone: 'real' | 'token'
  hot: boolean
}

function Chip({ entity, text, tone, hot }: ChipProps) {
  const hover = () => {
    store.hover(entity.token)
  }
  const leave = () => {
    store.hover(null)
  }
  return (
    <span
      className={`chip chip--${tone}`}
      data-hot={hot}
      data-risk={entity.riskLevel}
      data-phi={isPhi(entity.hipaaCategory)}
      onMouseEnter={hover}
      onMouseLeave={leave}
      title={chipTitle(entity)}
    >
      {text}
    </span>
  )
}
