import { memo, useState } from 'react'
import { LIMITS, store, type TrafficEntry } from '../engine/store'
import { clockOf, formatBytes, typeTag } from '../engine/text'
import { useStore } from '../engine/useStore'
import { Ticker } from './Ticker'

/**
 * Every request the proxy saw, newest first, marked with what it did about it.
 * Treated rows are the ones playing out on the band above. Click one to hold it
 * in the inspector. The "treated" toggle hides everything else, and reaches
 * further back: the store keeps treated rows long after the noise around them
 * has been evicted.
 */
export function Traffic() {
  const traffic = useStore((state) => state.traffic)
  const { requests, treated } = useStore((state) => state.metrics)
  const pinnedId = useStore((state) => state.pinnedId)
  const hoveredDevice = useStore((state) => state.hoveredDevice)
  const [treatedOnly, setTreatedOnly] = useState(false)

  const rows = treatedOnly
    ? traffic.filter((entry) => entry.treatment === 'treated').slice(0, LIMITS.treatedTraffic)
    : traffic.slice(0, LIMITS.traffic)

  return (
    <section className="panel" aria-label="All requests through the proxy">
      <div className="panel__head">
        <span className="u-label">Traffic</span>
        <span className="panel__note">
          {requests} seen ·{' '}
          <button
            type="button"
            className="traffic__treated traffic__filter"
            aria-pressed={treatedOnly}
            title={treatedOnly ? 'Show all requests' : 'Show treated requests only'}
            onClick={() => setTreatedOnly((on) => !on)}
          >
            {treated} treated
          </button>
        </span>
      </div>

      <div className="panel__body">
        {rows.length === 0 && (
          <p className="matrix__empty panel__empty">
            {treatedOnly ? 'Nothing has been treated yet.' : 'Nothing has crossed yet.'}
          </p>
        )}
        {rows.map((entry) => (
          <Row
            key={entry.requestId}
            entry={entry}
            pinned={entry.exchangeId !== undefined && pinnedId === entry.exchangeId}
            deviceHot={hoveredDevice === entry.clientLabel}
          />
        ))}
      </div>

      <Ticker />
    </section>
  )
}

interface RowProps {
  entry: TrafficEntry
  pinned: boolean
  deviceHot: boolean
}

const Row = memo(function Row({ entry, pinned, deviceHot }: RowProps) {
  const treated = entry.treatment === 'treated'
  const brief = [
    entry.status ? `${entry.status}` : '…',
    `${formatBytes(entry.requestBytes)} → ${formatBytes(entry.responseBytes)}`,
    entry.durationMs === undefined ? '' : `${Math.round(entry.durationMs)} ms`,
  ]
    .filter(Boolean)
    .join(' · ')
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
      <span className="tr__detail">{brief}</span>
      <span className={`tr__mark tr__mark--${entry.treatment}`}>{markOf(entry)}</span>
      {/* The title tooltip is mouse-only; say the same thing to assistive tech. */}
      <span className="u-sr-only">{detail}</span>
    </>
  )

  if (!treated) {
    return (
      <div
        className="tr"
        data-treatment={entry.treatment}
        data-device-hot={deviceHot}
        title={detail}
      >
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
      data-device-hot={deviceHot}
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
