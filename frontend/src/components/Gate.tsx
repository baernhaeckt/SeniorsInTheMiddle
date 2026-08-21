import { Morph } from './Morph'
import { excerptAround } from '../engine/text'
import type { Exchange, Stage } from '../engine/store'
import type { ProxyInfo } from '../engine/store'

const STEPS: { key: string; stages: Stage[]; tone: 'warm' | 'alert' | 'cool' }[] = [
  { key: 'read', stages: ['ingress'], tone: 'warm' },
  { key: 'find', stages: ['inspect'], tone: 'alert' },
  { key: 'hold', stages: ['redact'], tone: 'alert' },
  { key: 'send', stages: ['egress', 'thinking'], tone: 'cool' },
  { key: 'receive', stages: ['return'], tone: 'cool' },
  { key: 'restore', stages: ['rehydrate', 'deliver'], tone: 'warm' },
]

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

/**
 * The readout always frames the same identifier, so the part that changes stays
 * in the same place on screen.
 */
function readoutOf(exchange: Exchange | null): Readout {
  if (!exchange) return { kind: 'idle', label: 'idle', meta: '—' }

  const first = exchange.entities[0]
  const count = exchange.entities.length

  const build = (
    label: string,
    meta: string,
    text: string,
    focus: string,
    focusClass: string,
  ): Readout => {
    const parts = excerptAround(text, focus, 108)
    return {
      kind: 'text',
      label,
      meta,
      before: parts.before,
      focus: parts.focus,
      after: parts.after,
      focusClass,
    }
  }

  switch (exchange.stage) {
    case 'ingress':
      return build('reading request', exchange.id, exchange.requestBody, first?.value ?? '', 'tm__real')

    case 'inspect':
      return build(
        'identifiers found',
        `${count} · ${exchange.scannedMs ?? 0} ms`,
        exchange.requestBody,
        first?.value ?? '',
        'tm__pii',
      )

    case 'redact':
    case 'egress':
      return build(
        'held at the boundary',
        `${count} value${count === 1 ? '' : 's'}`,
        exchange.redactedRequestBody ?? exchange.requestBody,
        first?.token ?? '',
        
        'tm__token',
      )

    case 'thinking':
      return build(
        'sent to destination',
        exchange.target ?? exchange.host,
        exchange.redactedRequestBody ?? exchange.requestBody,
        first?.token ?? '',
        
        'tm__token',
      )

    case 'return':
      return build(
        'response received',
        `${exchange.upstreamMs ?? 0} ms`,
        exchange.tokenizedResponseBody ?? '',
        first?.token ?? '',
        
        'tm__token',
      )

    case 'rehydrate':
    case 'deliver':
      return build(
        'restored for the client',
        `${count} value${count === 1 ? '' : 's'}`,
        exchange.responseBody ?? '',
        first?.value ?? '',
        
        'tm__real',
      )

    default:
      return { kind: 'idle', label: 'idle', meta: '—' }
  }
}
