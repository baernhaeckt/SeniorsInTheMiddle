import { store, type Exchange, type PrivacyVerdict } from '../engine/store'
import { useStore } from '../engine/useStore'
import type { ProxyPolicy } from '../protocol/types'

interface InsightsProps {
  exchange: Exchange | null
  policy: ProxyPolicy | null
}

/**
 * What the detectors know about one exchange beyond the bodies: how sure they
 * were, what kinds they found, what they left alone, and -- once the slow check
 * has answered -- how recoverable the names still are from the redacted text.
 */
export function Insights({ exchange, policy }: InsightsProps) {
  const hoveredToken = useStore((state) => state.hoveredToken)

  if (!exchange) return null

  const kinds = Object.entries(exchange.typeFrequencies).sort((a, b) => b[1] - a[1])
  const most = kinds[0]?.[1] ?? 1
  const threshold = policy?.confidenceThreshold

  return (
    <div className="insights" aria-label="Detection insights">
      <div className="insight">
        <span className="u-label insight__label">confidence</span>
        <Ring value={exchange.riskScoreMean} />
      </div>

      <div className="insight insight--kinds">
        <span className="u-label insight__label">
          kinds
          {exchange.suppressed > 0 && (
            <span
              className="insight__hint"
              title="found inside another finding, or not placeable on the text"
            >
              {' '}
              · {exchange.suppressed} suppressed
            </span>
          )}
        </span>
        {kinds.length === 0 ? (
          <span className="insight__none">none</span>
        ) : (
          <ul className="kindbars">
            {kinds.map(([kind, count]) => (
              <li key={kind} className="kindbar" data-risk={riskOf(exchange, kind)}>
                <span className="kindbar__name">{kind.toLowerCase()}</span>
                <span className="kindbar__track">
                  <span className="kindbar__fill" style={{ width: `${(count / most) * 100}%` }} />
                </span>
                <span className="kindbar__n">{count}</span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="insight insight--misses">
        <span className="u-label insight__label">
          near misses
          {threshold !== undefined && (
            <span className="insight__hint"> · below {Math.round(threshold * 100)}%</span>
          )}
        </span>
        {exchange.nearMisses.length === 0 ? (
          <span className="insight__none">none</span>
        ) : (
          <span className="misses">
            {exchange.nearMisses.map((miss, index) => (
              <span
                key={`${miss.kind}-${index}`}
                className="miss"
                title={`${miss.kind} · ${Math.round(miss.confidence * 100)}% · left in place`}
              >
                {miss.value}
                <small>{Math.round(miss.confidence * 100)}%</small>
              </span>
            ))}
          </span>
        )}
      </div>

      <div className="insight insight--privacy">
        <span className="u-label insight__label">re-identification</span>
        <Privacy verdict={exchange.privacy} hoveredToken={hoveredToken} />
      </div>
    </div>
  )
}

function riskOf(exchange: Exchange, kind: string): number {
  return exchange.entities.find((entity) => entity.kind === kind)?.riskLevel ?? 0
}

/** A 0..1 number as a ring, the number inside it. */
function Ring({ value }: { value: number | undefined }) {
  const radius = 14
  const circumference = 2 * Math.PI * radius
  const filled = value === undefined ? 0 : value * circumference

  return (
    <span className="ring" data-empty={value === undefined}>
      <svg viewBox="0 0 36 36" width="36" height="36" aria-hidden="true">
        <circle className="ring__track" cx="18" cy="18" r={radius} />
        <circle
          className="ring__fill"
          cx="18"
          cy="18"
          r={radius}
          strokeDasharray={`${filled} ${circumference - filled}`}
          transform="rotate(-90 18 18)"
        />
      </svg>
      <span className="ring__value">
        {value === undefined ? '—' : `${Math.round(value * 100)}`}
      </span>
    </span>
  )
}

function Privacy({
  verdict,
  hoveredToken,
}: {
  verdict: PrivacyVerdict | undefined
  hoveredToken: string | null
}) {
  if (!verdict) {
    return (
      <span className="privacy privacy--pending">
        <span className="privacy__pulse" aria-hidden="true" />
        checking how recoverable the names are…
      </span>
    )
  }

  if (verdict.status !== 'ok') {
    return (
      <span className="privacy" data-status={verdict.status}>
        {verdict.status === 'skipped' ? 'not checked' : 'check failed'}
        {verdict.reason && <small> · {verdict.reason}</small>}
      </span>
    )
  }

  const tier = verdict.maxProbability >= 0.7 ? 3 : verdict.maxProbability >= 0.4 ? 2 : 1
  return (
    <span className="privacy" data-status="ok" data-tier={tier}>
      <span className="privacy__max" title={`answered in ${Math.round(verdict.assessedMs)} ms`}>
        {Math.round(verdict.maxProbability * 100)}%
      </span>
      <span className="privacy__bars">
        {verdict.risks.map((risk) => (
          <span
            key={risk.token}
            className="privacy__bar"
            data-hot={hoveredToken === risk.token}
            onMouseEnter={() => {
              store.hover(risk.token)
            }}
            onMouseLeave={() => {
              store.hover(null)
            }}
            title={`${risk.token} · ${Math.round(risk.probability * 100)}% recoverable from context`}
          >
            <span className="privacy__token">{risk.token}</span>
            <span className="privacy__track">
              <span className="privacy__fill" style={{ width: `${risk.probability * 100}%` }} />
            </span>
          </span>
        ))}
      </span>
    </span>
  )
}
