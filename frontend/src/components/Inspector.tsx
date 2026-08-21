import { prettyBody, splitByValues } from '../engine/text'
import { store, type AppState, type Exchange } from '../engine/store'

export function Inspector({ state }: { state: AppState }) {
  // Follow the newest treated exchange that has been round-tripped, so all four
  // cells hold something to read. Pin any other one from the traffic list.
  const shown =
    state.exchanges.find((exchange) => exchange.id === state.pinnedId) ??
    state.exchanges.find((exchange) => exchange.responseBody) ??
    state.exchanges[0] ??
    null

  return (
    <section className="panel" aria-label="Payload inspector">
      <div className="panel__head">
        <span className="u-label">Payload inspector</span>
        <div className="hist">
          {shown && (
            <span className="panel__note">
              {shown.method} {shown.host}
              {shown.path}
            </span>
          )}
          <button
            type="button"
            className="hist__btn"
            data-active={state.pinnedId === null}
            onClick={() => store.pin(null)}
          >
            {state.pinnedId === null ? 'following live' : 'follow live'}
          </button>
        </div>
      </div>

      <div className="panel__body">
        <div className="matrix">
          <div className="matrix__corner" />
          <div className="matrix__colhead">
            <span className="u-label" style={{ color: 'var(--warm)' }}>
              What the client sent
            </span>
          </div>
          <div className="matrix__colhead matrix__colhead--open">
            <span className="u-label" style={{ color: 'var(--cool)' }}>
              What the destination saw
            </span>
            <span className="panel__note">{shown?.contentType ?? ''}</span>
          </div>

          <div className="matrix__rowhead">
            <span>Outbound</span>
          </div>
          <Cell exchange={shown} state={state} field="requestBody" tone="real" />
          <Cell exchange={shown} state={state} field="redactedRequestBody" tone="token" open />

          <div className="matrix__rowhead matrix__row2">
            <span>Inbound</span>
          </div>
          <Cell exchange={shown} state={state} field="responseBody" tone="real" row2 />
          <Cell exchange={shown} state={state} field="tokenizedResponseBody" tone="token" open row2 />
        </div>
      </div>
    </section>
  )
}

type BodyField = 'requestBody' | 'redactedRequestBody' | 'responseBody' | 'tokenizedResponseBody'

interface CellProps {
  exchange: Exchange | null
  state: AppState
  field: BodyField
  tone: 'real' | 'token'
  open?: boolean
  row2?: boolean
}

const WAITING: Record<BodyField, string> = {
  requestBody: 'waiting for a request worth reading',
  redactedRequestBody: 'not rewritten yet',
  responseBody: 'nothing delivered back yet',
  tokenizedResponseBody: 'destination has not answered yet',
}

function Cell({ exchange, state, field, tone, open, row2 }: CellProps) {
  const raw = exchange?.[field]
  const className = [
    'matrix__cell',
    open ? 'matrix__cell--open' : '',
    row2 ? 'matrix__row2' : '',
  ].join(' ')

  if (!raw) {
    return (
      <div className={className}>
        <span className="matrix__empty">{WAITING[field]}</span>
      </div>
    )
  }

  const text = prettyBody(raw, exchange?.contentType)
  const runs = splitByValues(text, exchange?.entities ?? [], tone === 'token')

  return (
    <div className={className}>
      {runs.map((run, index) =>
        run.entity ? (
          <span
            key={index}
            className={`chip chip--${tone}`}
            data-hot={state.hoveredToken === run.entity.token}
            onMouseEnter={() => store.hover(run.entity!.token)}
            onMouseLeave={() => store.hover(null)}
            title={`${run.entity.kind} · ${Math.round(run.entity.confidence * 100)}% confidence`}
          >
            {run.text}
          </span>
        ) : (
          <span key={index}>{run.text}</span>
        ),
      )}
    </div>
  )
}
