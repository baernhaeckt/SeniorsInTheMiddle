import type { ExchangeTiming } from '../protocol/types'

const STEPS: {
  key: keyof ExchangeTiming
  label: string
  tone: 'warm' | 'alert' | 'cool' | 'dim'
}[] = [
  { key: 'bufferMs', label: 'read', tone: 'warm' },
  { key: 'detectMs', label: 'scan', tone: 'alert' },
  { key: 'upstreamMs', label: 'upstream', tone: 'cool' },
  { key: 'rehydrateMs', label: 'restore', tone: 'warm' },
  { key: 'overheadMs', label: 'other', tone: 'dim' },
]

interface WaterfallProps {
  timing: ExchangeTiming | undefined
  totalMs: number | undefined
}

/**
 * Where a round trip went, as one bar: the proxy's own share next to the time
 * spent waiting on the destination. Usually the bar is nearly all upstream,
 * which is the point of showing it.
 */
export function Waterfall({ timing, totalMs }: WaterfallProps) {
  const total = totalMs ?? (timing ? STEPS.reduce((sum, step) => sum + timing[step.key], 0) : 0)

  return (
    <div className="waterfall" data-empty={!timing}>
      <div className="waterfall__bar" aria-hidden="true">
        {timing &&
          total > 0 &&
          STEPS.map((step) => (
            <span
              key={step.key}
              className="waterfall__seg"
              data-tone={step.tone}
              style={{ width: `${(timing[step.key] / total) * 100}%` }}
            />
          ))}
      </div>
      <div className="waterfall__legend">
        {STEPS.map((step) => (
          <span key={step.key} className="waterfall__item" data-tone={step.tone}>
            <span className="waterfall__swatch" aria-hidden="true" />
            <span className="u-label">{step.label}</span>
            <span className="waterfall__ms">
              {timing ? `${Math.round(timing[step.key])}` : '—'}
            </span>
          </span>
        ))}
      </div>
    </div>
  )
}
