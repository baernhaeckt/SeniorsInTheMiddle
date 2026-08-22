import { memo } from 'react'
import { store, type LogLine } from '../engine/store'
import { clockOf } from '../engine/text'
import { useStore } from '../engine/useStore'

/** How many lines the ticker shows. The store keeps more for anyone who scrolls. */
const SHOWN = 8

/**
 * What the proxy said, newest first, with its level showing. A line that names
 * an exchange is a button: click it to hold that exchange in the inspector.
 */
export function Ticker() {
  const logs = useStore((state) => state.logs)
  const pinnedId = useStore((state) => state.pinnedId)

  return (
    <div className="ticker" role="log" aria-label="What the proxy said">
      {logs.length === 0 && <span className="ticker__empty">waiting for the proxy</span>}
      {logs.slice(0, SHOWN).map((line) => (
        <Line
          key={line.seq}
          line={line}
          pinned={line.exchangeId !== undefined && line.exchangeId === pinnedId}
        />
      ))}
    </div>
  )
}

const Line = memo(function Line({ line, pinned }: { line: LogLine; pinned: boolean }) {
  const inner = (
    <>
      <span className="ticker__at">{clockOf(line.at)}</span>
      <span className="ticker__level">{line.level}</span>
      <span className="ticker__msg">{line.message}</span>
    </>
  )

  if (!line.exchangeId) {
    return (
      <div className="ticker__line" data-level={line.level}>
        {inner}
      </div>
    )
  }

  const exchangeId = line.exchangeId
  return (
    <button
      type="button"
      className="ticker__line"
      data-level={line.level}
      data-active={pinned}
      aria-pressed={pinned}
      title={`hold ${exchangeId} in the inspector`}
      onClick={() => {
        store.pin(pinned ? null : exchangeId)
      }}
    >
      {inner}
    </button>
  )
})
