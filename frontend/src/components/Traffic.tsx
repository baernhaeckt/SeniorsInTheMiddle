import { clockOf, formatBytes } from '../engine/text'
import { store, type AppState, type TrafficEntry } from '../engine/store'

/**
 * Every request the proxy saw, newest first, marked with what it did about it.
 * Treated rows are the ones playing out on the band above. Click one to hold it
 * in the inspector.
 */
export function Traffic({ state }: { state: AppState }) {
  const { requests, treated } = state.metrics

  return (
    <section className="panel" aria-label="All requests through the proxy">
      <div className="panel__head">
        <span className="u-label">Traffic</span>
        <span className="panel__note">
          {requests} seen · <span className="traffic__treated">{treated} treated</span>
        </span>
      </div>

      <div className="panel__body">
        {state.traffic.length === 0 && (
          <p className="matrix__empty" style={{ padding: '10px 12px' }}>
            Nothing has crossed yet.
          </p>
        )}
        {state.traffic.map((entry) => (
          <Row key={entry.requestId} entry={entry} pinned={state.pinnedId === entry.exchangeId} />
        ))}
      </div>

      <div className="traffic__foot">{state.lastLog ?? 'waiting for the proxy'}</div>
    </section>
  )
}

function Row({ entry, pinned }: { entry: TrafficEntry; pinned: boolean }) {
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
      title={detail}
      onClick={() => store.pin(pinned ? null : (entry.exchangeId ?? null))}
    >
      {inner}
    </button>
  )
}

function markOf(entry: TrafficEntry): string {
  if (entry.treatment === 'treated') {
    return entry.identifiers === undefined ? 'scanning' : `${entry.identifiers} PII`
  }
  if (entry.treatment === 'clean') return 'clean'
  return typeTag(entry.contentType)
}

/** Short, honest label for why a request was waved past. */
function typeTag(contentType?: string): string {
  if (!contentType) return 'asset'
  if (contentType.includes('css')) return 'css'
  if (contentType.includes('javascript')) return 'js'
  if (contentType.startsWith('font/')) return 'font'
  if (contentType.startsWith('image/')) return 'image'
  if (contentType.includes('json')) return 'json'
  return contentType.split('/')[1] ?? 'asset'
}
