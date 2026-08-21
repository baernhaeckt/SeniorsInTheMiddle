import { proxyAddress } from '../config'
import { median, type AppState } from '../engine/store'

const LINK_COPY: Record<string, string> = {
  idle: 'not attached',
  connecting: 'attaching',
  live: 'live',
  retrying: 'reattaching',
  closed: 'detached',
}

export function Header({ state, onOpenSetup }: { state: AppState; onOpenSetup: () => void }) {
  const { metrics, link } = state
  const p50 = median(metrics.latencies)

  return (
    <header className="head">
      <div className="head__id">
        <svg className="head__mark" viewBox="0 0 32 32" fill="none" aria-hidden="true">
          <path
            d="M16 2 4 7v9c0 7.2 5 12.4 12 14 7-1.6 12-6.8 12-14V7L16 2Z"
            stroke="var(--warm)"
            strokeWidth="1.4"
          />
          <path d="M16 2v28c7-1.6 12-6.8 12-14V7L16 2Z" fill="var(--cool)" opacity=".14" />
          <path d="M16 2v28" stroke="var(--ink)" strokeWidth="1" opacity=".7" />
          <rect x="11.5" y="14" width="9" height="3.6" rx="1" fill="var(--alert)" />
        </svg>
        <div>
          <h1 className="head__name u-display">
            Seniors in the <em>Middle</em>
          </h1>
          <div className="u-label head__sub">Transparent http/https proxy · Bärn Häckt 2026</div>
        </div>
      </div>

      <button type="button" className="setupcta" onClick={onOpenSetup}>
        <span className="setupcta__label">Proxy address</span>
        <span className="setupcta__addr">{proxyAddress}</span>
        <span className="setupcta__go" aria-hidden="true">
          &rarr;
        </span>
      </button>

      <div className="head__right">
        <div className="stats">
          <Stat label="Requests" value={metrics.requests} />
          <Stat label="Treated" value={metrics.treated} variant="treated" />
          <Stat label="Identifiers held" value={metrics.identifiersHeld} variant="held" />
          <Stat label="Leaked" value={metrics.leaks} variant="leaks" />
          <Stat label="Round trip" value={p50 ?? '—'} unit={p50 === null ? '' : 'ms'} />
        </div>

        <div className="link" data-state={link.state} title={link.detail ?? link.endpoint}>
          <span className="link__dot" />
          <span>
            {LINK_COPY[link.state] ?? link.state} · {link.endpoint}
          </span>
        </div>
      </div>
    </header>
  )
}

interface StatProps {
  label: string
  value: number | string
  unit?: string
  variant?: 'held' | 'leaks' | 'treated'
}

function Stat({ label, value, unit, variant }: StatProps) {
  return (
    <div className={`stat${variant ? ` stat--${variant}` : ''}`}>
      <div className="stat__value">
        {value}
        {unit ? <small>{unit}</small> : null}
      </div>
      <div className="u-label stat__label">{label}</div>
    </div>
  )
}
