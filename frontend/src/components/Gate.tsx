import { isTokenizedStage, stageViewOf } from '../engine/stageView'
import type { Exchange, ProxyInfo, Stage } from '../engine/store'
import { formatBytes, readoutWindow, type ReadoutLine } from '../engine/text'
import type { GateCard } from '../engine/useGateStack'
import { cssVars } from '../ui/cssVars'
import { Clip } from './Clip'
import { Morph } from './Morph'
import { Waterfall } from './Waterfall'

type Tone = 'warm' | 'alert' | 'cool'

const STEPS: { key: string; stages: Stage[]; tone: Tone }[] = [
  { key: 'read', stages: ['ingress'], tone: 'warm' },
  { key: 'find', stages: ['inspect'], tone: 'alert' },
  { key: 'hold', stages: ['redact'], tone: 'alert' },
  { key: 'send', stages: ['egress', 'thinking'], tone: 'cool' },
  { key: 'receive', stages: ['return'], tone: 'cool' },
  { key: 'restore', stages: ['rehydrate', 'deliver'], tone: 'warm' },
]

/** One word per stage, for a card waiting its turn. */
const WAITING: Record<Stage, string> = {
  ingress: 'reading',
  inspect: 'scanning',
  redact: 'holding',
  egress: 'sending',
  thinking: 'awaiting reply',
  return: 'replied',
  rehydrate: 'restoring',
  deliver: 'delivering',
  done: 'delivered',
}

const TONES: Record<Stage, Tone> = {
  ingress: 'warm',
  inspect: 'alert',
  redact: 'alert',
  egress: 'cool',
  thinking: 'cool',
  return: 'cool',
  rehydrate: 'warm',
  deliver: 'warm',
  done: 'warm',
}

/** Lines the readout shows. Mirrors --lines on `.gate__readout` in band.css. */
const READOUT_LINES = 4

/**
 * How much of the identifier's own line is kept either side of it. Wide enough
 * that a short line arrives whole; narrow enough that a long one cannot carry
 * the identifier off the right-hand edge.
 */
const READOUT_WIDTH = 64

interface GateProps {
  cards: GateCard[]
  proxy: ProxyInfo | null
}

/**
 * The boundary itself, and everything the proxy knows about what is crossing it.
 *
 * One card holds the front until its hold runs out (see `gateStack.ts`); the
 * requests that arrived meanwhile wait as a stack of strips underneath, newest
 * at the bottom, and step up as the one above them leaves.
 */
export function Gate({ cards, proxy }: GateProps) {
  const here = cards.filter((card) => !card.leaving)
  const front = here[0] ?? null
  const queue = here.slice(1)
  const leaving = cards.filter((card) => card.leaving)

  return (
    <div className="gatestack">
      {leaving.map((card) => (
        <Card key={card.exchange.id} exchange={card.exchange} proxy={proxy} leaving />
      ))}

      <Card key={front?.exchange.id ?? 'idle'} exchange={front?.exchange ?? null} proxy={proxy} />

      {queue.length > 0 && (
        <div className="gatestack__queue">
          {queue.map((card, index) => (
            <Waiting key={card.exchange.id} exchange={card.exchange} depth={index} />
          ))}
        </div>
      )}
    </div>
  )
}

interface CardProps {
  exchange: Exchange | null
  proxy: ProxyInfo | null
  leaving?: boolean
}

/**
 * Every row keeps its height whether or not there is anything to fill it: this
 * sits in the middle of a wall display, and a box that grows and shrinks as
 * requests come and go reads as a fault. Missing numbers show as a dash.
 */
function Card({ exchange, proxy, leaving = false }: CardProps) {
  const reading = readoutOf(exchange)
  const kinds = kindsOf(exchange)

  return (
    <article
      className={leaving ? 'gate gate--leaving' : 'gate'}
      data-held={exchange?.stage === 'done'}
    >
      <div className="gate__top">
        <span className="gate__name">SITM Gate</span>
        <span className="gate__policy">{proxy?.policy ?? 'no policy'}</span>
      </div>

      <div className="gate__route">
        <span className="gate__method" data-idle={exchange === null}>
          {exchange?.method ?? '—'}
        </span>
        {exchange ? (
          <Clip
            className="gate__url"
            value={`${exchange.scheme}://${exchange.host}${exchange.path}`}
          />
        ) : (
          <span className="gate__url gate__url--idle">nothing at the boundary</span>
        )}
      </div>

      <div className="gate__stagerow">
        {STEPS.map((step) => (
          <span
            key={step.key}
            className="gate__step"
            data-on={exchange && step.stages.includes(exchange.stage) ? step.tone : undefined}
          />
        ))}
      </div>

      <div className="gate__steplabel">
        <span className="gate__stage">{reading.label}</span>
        <span className="gate__meta">{reading.meta}</span>
      </div>

      <div className="gate__readout" data-laidout={reading.kind === 'text' && reading.structured}>
        {reading.kind === 'idle' ? (
          <span className="gate__idle">boundary idle · assets passing untouched</span>
        ) : (
          reading.lines.map((line) => (
            <div key={line.key} className="gate__line">
              {line.runs.map((run, index) =>
                run.key === undefined ? (
                  <span key={`t-${index}`}>{run.text}</span>
                ) : (
                  <Morph key={run.key} to={run.text} settledClass={reading.focusClass} />
                ),
              )}
            </div>
          ))
        )}
      </div>

      <div className="gate__kinds">
        {kinds.length === 0 ? (
          <span className="gate__nokinds">no identifiers held</span>
        ) : (
          kinds.map((entry) => (
            <span key={entry.kind} className="gate__kind" data-risk={entry.risk}>
              {entry.kind.toLowerCase()}
              {entry.count > 1 && <small>&times;{entry.count}</small>}
            </span>
          ))
        )}
      </div>

      <div className="gate__feet">
        <Foot label="total" value={msOf(exchange?.totalMs ?? exchange?.upstreamMs)} />
        <Foot label="payload" value={formatBytes(exchange?.bytes)} />
        <Foot
          label="status"
          value={exchange?.status === undefined ? '—' : String(exchange.status)}
        />
        <Foot
          label="restored"
          value={exchange?.restored === undefined ? '—' : String(exchange.restored)}
        />
      </div>

      <div className="gate__timing">
        <Waterfall timing={exchange?.timing} totalMs={exchange?.totalMs} />
      </div>
    </article>
  )
}

interface WaitingProps {
  exchange: Exchange
  /** How far back in the stack, so each card sits a little narrower and fainter. */
  depth: number
}

function Waiting({ exchange, depth }: WaitingProps) {
  return (
    <div
      className="gateq"
      data-tone={TONES[exchange.stage]}
      style={cssVars({ '--depth': String(depth) })}
    >
      <span className="gateq__method">{exchange.method}</span>
      <Clip className="gateq__url" value={`${exchange.host}${exchange.path}`} />
      <span className="gateq__stage">{WAITING[exchange.stage]}</span>
    </div>
  )
}

interface FootProps {
  label: string
  value: string
}

function Foot({ label, value }: FootProps) {
  return (
    <div className="gate__foot">
      <span className="gate__footvalue">{value}</span>
      <span className="u-label gate__footlabel">{label}</span>
    </div>
  )
}

function msOf(value: number | undefined): string {
  return value === undefined ? '—' : `${Math.round(value)} ms`
}

interface KindCount {
  kind: string
  count: number
  /** Highest risk level among the entities of this kind. */
  risk: number
}

/** What is being held, by category, in the order the proxy found them. */
function kindsOf(exchange: Exchange | null): KindCount[] {
  if (!exchange) return []
  const counts = new Map<string, { count: number; risk: number }>()
  for (const entity of exchange.entities) {
    const prior = counts.get(entity.kind) ?? { count: 0, risk: 0 }
    counts.set(entity.kind, {
      count: prior.count + 1,
      risk: Math.max(prior.risk, entity.riskLevel),
    })
  }
  return [...counts].map(([kind, { count, risk }]) => ({ kind, count, risk }))
}

type Readout =
  | { kind: 'idle'; label: string; meta: string }
  | {
      kind: 'text'
      label: string
      meta: string
      structured: boolean
      lines: ReadoutLine[]
      focusClass: string
    }

const IDLE: Readout = { kind: 'idle', label: 'idle', meta: '—' }

/** What the label row says at each stage. The body and focus come from `stageViewOf`. */
function labelOf(exchange: Exchange): { label: string; meta: string; focusClass: string } {
  const count = exchange.entities.length
  const values = `${count} value${count === 1 ? '' : 's'}`
  switch (exchange.stage) {
    case 'ingress':
      return { label: 'reading request', meta: exchange.id, focusClass: 'tm__real' }
    case 'inspect':
      return {
        label: 'identifiers found',
        meta: `${count} · ${exchange.scannedMs ?? 0} ms`,
        focusClass: 'tm__pii',
      }
    case 'redact':
    case 'egress':
      return { label: 'held at the boundary', meta: values, focusClass: 'tm__token' }
    case 'thinking':
      return {
        label: 'sent to destination',
        meta: exchange.target ?? exchange.host,
        focusClass: 'tm__token',
      }
    case 'return':
      return {
        label: 'response received',
        meta: `${exchange.upstreamMs ?? 0} ms`,
        focusClass: 'tm__token',
      }
    case 'rehydrate':
    case 'deliver':
      return { label: 'restored for the client', meta: values, focusClass: 'tm__real' }
    case 'done':
      // The card is held rather than cleared, so a finished exchange is still
      // there to be read once the animation has stopped moving.
      return {
        label: 'delivered to the client',
        meta: msOf(exchange.totalMs),
        focusClass: 'tm__real',
      }
  }
}

/**
 * The readout always frames the same identifier, so the part that changes stays
 * in the same place on screen.
 */
function readoutOf(exchange: Exchange | null): Readout {
  if (!exchange) return IDLE
  const view = stageViewOf(exchange)
  const window = readoutWindow(
    view.text,
    exchange.entities,
    isTokenizedStage(exchange.stage),
    READOUT_LINES,
    READOUT_WIDTH,
  )
  return { kind: 'text', ...labelOf(exchange), ...window }
}
