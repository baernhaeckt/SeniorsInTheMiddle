import { memo } from 'react'
import { store, type TrafficEntry } from '../engine/store'
import { clockOf, formatBytes, typeTag } from '../engine/text'
import { useStore } from '../engine/useStore'

/**
 * Every request the proxy saw, newest first, marked with what it did about it.
 * Treated rows are the ones playing out on the band above. Click one to hold it
 * in the inspector.
 */
export function Traffic() {
  const traffic = useStore((state) => state.traffic)
  const { requests, treated } = useStore((state) => state.metrics)
  const pinnedId = useStore((state) => state.pinnedId)
  const lastLog = useStore((state) => state.lastLog)

  return (
    <section className="panel" aria-label="All requests through the proxy">
      <div className="panel__head">
        <span className="u-label">Traffic</span>
        <span className="panel__note">
          {requests} seen · <span className="traffic__treated">{treated} treated</span>
        </span>
      </div>

      <div className="panel__body">
        {traffic.length === 0 && (
          <p className="matrix__empty panel__empty">Nothing has crossed yet.</p>
        )}
        {traffic.map((entry) => (
          <Row
            key={entry.requestId}
            entry={entry}
            pinned={entry.exchangeId !== undefined && pinnedId === entry.exchangeId}
          />
        ))}
      </div>

      <div className="traffic__foot">{lastLog ?? 'waiting for the proxy'}</div>
    </section>
  )
}

const Row = memo(function Row({ entry, pinned }: { entry: TrafficEntry; pinned: boolean }) {
  const treated = entry.treatment === 'treated'
  const detail = [
    entry.status ? `${entry.status}` : 'in flight',
    formatBytes(entry.responseBytes),
    entry.durationMs === undefined ? '' : `${entry.durationMs} ms`,
    entry.clientLabel,
    entry.reason,
  ]
    .filter(Boolean)
    .join(' · ')

  const inner = (
    <>
      <span className="tr__at">{clockOf(entry.at)}</span>
      <span className="tr__method">{entry.method}</span>
      <span className="tr__url">
        <b>{entry.host}</b>
        {entry.path}
      </span>
      <span className={`tr__mark tr__mark--${entry.treatment}`}>{markOf(entry)}</span>
      {/* The title tooltip is mouse-only; say the same thing to assistive tech. */}
      <span className="u-sr-only">{detail}</span>
    </>
  )

  if (!treated) {
    return (
      <div className="tr" data-treatment={entry.treatment} title={detail}>
        {inner}
      </div>
    )
  }

  return (
    <button
      type="button"
      className="tr"
      data-treatment="treated"
      data-active={pinned}
      aria-pressed={pinned}
      title={detail}
      onClick={() => {
        store.pin(pinned ? null : (entry.exchangeId ?? null))
      }}
    >
      {inner}
    </button>
  )
})

function markOf(entry: TrafficEntry): string {
  if (entry.treatment === 'treated') {
    return entry.identifiers === undefined ? 'scanning' : `${entry.identifiers} PII`
  }
  if (entry.treatment === 'clean') return 'clean'
  return typeTag(entry.contentType)
}
