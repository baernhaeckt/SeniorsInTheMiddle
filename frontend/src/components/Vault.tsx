import { store, type AppState } from '../engine/store'

export function Vault({ state }: { state: AppState }) {
  return (
    <section className="panel" aria-label="Values held at the boundary">
      <div className="panel__head">
        <span className="u-label">Held at the boundary</span>
        <span className="vault__count">{state.vault.length} mapped</span>
      </div>

      <div className="panel__body">
        {state.vault.length === 0 && (
          <p className="matrix__empty" style={{ padding: '10px 12px' }}>
            Nothing held yet.
          </p>
        )}

        {state.vault.map((record) => (
          <div key={record.token} className="vrow" data-hot={state.hoveredToken === record.token}>
            <span className="vrow__token">{record.token}</span>
            <span className="vrow__arrow">↔</span>
            <button
              type="button"
              className="vrow__value"
              onMouseEnter={() => store.hover(record.token)}
              onMouseLeave={() => store.hover(null)}
              onFocus={() => store.hover(record.token)}
              onBlur={() => store.hover(null)}
              title="Hover to read the real value"
            >
              {record.value}
            </button>
            <span className="vrow__kind">{record.kind}</span>
          </div>
        ))}
      </div>

      <div className="vault__seal">
        <LockGlyph />
        This table lives on the proxy. Hover a value to read it here.
      </div>
    </section>
  )
}

function LockGlyph() {
  return (
    <svg width="11" height="11" viewBox="0 0 12 12" fill="none" stroke="var(--warm)" strokeWidth="1.1">
      <rect x="2.2" y="5.2" width="7.6" height="5.4" rx="1" />
      <path d="M4 5.2V3.8a2 2 0 0 1 4 0v1.4" />
    </svg>
  )
}
