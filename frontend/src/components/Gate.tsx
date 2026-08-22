import { stageViewOf } from '../engine/stageView'
import type { Exchange, ProxyInfo, Stage } from '../engine/store'
import { excerptAround } from '../engine/text'
import { Morph } from './Morph'

const STEPS: { key: string; stages: Stage[]; tone: 'warm' | 'alert' | 'cool' }[] = [
  { key: 'read', stages: ['ingress'], tone: 'warm' },
  { key: 'find', stages: ['inspect'], tone: 'alert' },
  { key: 'hold', stages: ['redact'], tone: 'alert' },
  { key: 'send', stages: ['egress', 'thinking'], tone: 'cool' },
  { key: 'receive', stages: ['return'], tone: 'cool' },
  { key: 'restore', stages: ['rehydrate', 'deliver'], tone: 'warm' },
]

const READOUT_WIDTH = 108

interface GateProps {
  active: Exchange | null
  proxy: ProxyInfo | null
}

export function Gate({ active, proxy }: GateProps) {
  const reading = readoutOf(active)

  return (
    <div className="gate">
      <div className="gate__top">
        <span className="gate__name">SITM Gate</span>
        <span className="gate__policy">{proxy?.policy ?? 'no policy'}</span>
      </div>

      <div className="gate__stagerow">
        {STEPS.map((step) => (
          <span
            key={step.key}
            className="gate__step"
            data-on={active && step.stages.includes(active.stage) ? step.tone : undefined}
          />
        ))}
      </div>

      <div className="gate__steplabel">
        <span>{reading.label}</span>
        <span>{reading.meta}</span>
      </div>

      <div className="gate__readout">
        {reading.kind === 'idle' ? (
          <span className="gate__idle">boundary idle · assets passing untouched</span>
        ) : (
          <span>
            {reading.before}
            {reading.focus && <Morph to={reading.focus} settledClass={reading.focusClass} />}
            {reading.after}
          </span>
        )}
      </div>
    </div>
  )
}

type Readout =
  | { kind: 'idle'; label: string; meta: string }
  | {
      kind: 'text'
      label: string
      meta: string
      before: string
      focus: string
      after: string
      focusClass: string
    }

const IDLE: Readout = { kind: 'idle', label: 'idle', meta: '—' }

/** What the label row says at each stage. The body and focus come from `stageViewOf`. */
function labelOf(exchange: Exchange): { label: string; meta: string; focusClass: string } | null {
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
      return null
  }
}

/**
 * The readout always frames the same identifier, so the part that changes stays
 * in the same place on screen.
 */
function readoutOf(exchange: Exchange | null): Readout {
  if (!exchange) return IDLE
  const label = labelOf(exchange)
  if (!label) return IDLE
  const view = stageViewOf(exchange)
  const parts = excerptAround(view.text, view.focus, READOUT_WIDTH)
  return { kind: 'text', ...label, ...parts }
}
