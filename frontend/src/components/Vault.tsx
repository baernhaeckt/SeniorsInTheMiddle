import { memo } from 'react'
import { store, type VaultRecord } from '../engine/store'
import { useStore } from '../engine/useStore'
import { isPhi } from '../engine/entityFacts'

export function Vault() {
  const vault = useStore((state) => state.vault)
  const hoveredToken = useStore((state) => state.hoveredToken)

  return (
    <section className="panel" aria-label="Values held at the boundary">
      <div className="panel__head">
        <span className="u-label">Held at the boundary</span>
        <span className="vault__count">{vault.length} mapped</span>
      </div>

      <div className="panel__body">
        {vault.length === 0 && <p className="matrix__empty panel__empty">Nothing held yet.</p>}

        {vault.map((record) => (
          <VaultRow key={record.token} record={record} hot={hoveredToken === record.token} />
        ))}
      </div>

      <div className="vault__seal">
        <LockGlyph />
        This table lives on the proxy. Hover a value to read it here.
      </div>
    </section>
  )
}

const VaultRow = memo(function VaultRow({ record, hot }: { record: VaultRecord; hot: boolean }) {
  const hover = () => {
    store.hover(record.token)
  }
  const leave = () => {
    store.hover(null)
  }
  return (
    <div className="vrow" data-hot={hot} data-uses={record.uses > 1}>
      <span className="vrow__token">{record.token}</span>
      <span className="vrow__arrow" aria-hidden="true">
        ↔
      </span>
      <button
        type="button"
        className="vrow__value"
        onMouseEnter={hover}
        onMouseLeave={leave}
        onFocus={hover}
        onBlur={leave}
        title="Hover to read the real value"
      >
        {record.value}
      </button>
      {record.uses > 1 && <span className="vrow__uses">×{record.uses}</span>}
      <span
        className="vrow__kind"
        data-risk={record.riskLevel}
        data-phi={isPhi(record.hipaaCategory)}
        title={record.informationType || record.kind}
      >
        {record.kind}
      </span>
    </div>
  )
})

function LockGlyph() {
  return (
    <svg
      width="11"
      height="11"
      viewBox="0 0 12 12"
      fill="none"
      stroke="var(--warm)"
      strokeWidth="1.1"
      aria-hidden="true"
    >
      <rect x="2.2" y="5.2" width="7.6" height="5.4" rx="1" />
      <path d="M4 5.2V3.8a2 2 0 0 1 4 0v1.4" />
    </svg>
  )
}
