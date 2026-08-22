import { memo } from 'react'
import { shownExchange, store, type Exchange } from '../engine/store'
import { prettyBody, splitByValues } from '../engine/text'
import { useStore } from '../engine/useStore'
import type { Entity } from '../protocol/types'

export function Inspector() {
  const exchanges = useStore((state) => state.exchanges)
  const pinnedId = useStore((state) => state.pinnedId)
  const hoveredToken = useStore((state) => state.hoveredToken)
  const shown = shownExchange(exchanges, pinnedId)
  const following = pinnedId === null

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
            data-active={following}
            onClick={() => {
              store.pin(null)
            }}
          >
            {following ? 'following live' : 'follow live'}
          </button>
        </div>
      </div>

      <div className="panel__body">
        <div className="matrix">
          <div className="matrix__corner" />
          <div className="matrix__colhead">
            <span className="u-label u-label--warm">What the client sent</span>
          </div>
          <div className="matrix__colhead matrix__colhead--open">
            <span className="u-label u-label--cool">What the destination saw</span>
            <span className="panel__note">{shown?.contentType ?? ''}</span>
          </div>

          <div className="matrix__rowhead">
            <span>Outbound</span>
          </div>
          <Cell exchange={shown} hoveredToken={hoveredToken} field="requestBody" tone="real" />
          <Cell
            exchange={shown}
            hoveredToken={hoveredToken}
            field="redactedRequestBody"
            tone="token"
            open
          />

          <div className="matrix__rowhead matrix__row2">
            <span>Inbound</span>
          </div>
          <Cell
            exchange={shown}
            hoveredToken={hoveredToken}
            field="responseBody"
            tone="real"
            row2
          />
          <Cell
            exchange={shown}
            hoveredToken={hoveredToken}
            field="tokenizedResponseBody"
            tone="token"
            open
            row2
          />
        </div>
      </div>
    </section>
  )
}

type BodyField = 'requestBody' | 'redactedRequestBody' | 'responseBody' | 'tokenizedResponseBody'

interface CellProps {
  exchange: Exchange | null
  hoveredToken: string | null
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

const Cell = memo(function Cell({ exchange, hoveredToken, field, tone, open, row2 }: CellProps) {
  const raw = exchange?.[field]
  const className = ['matrix__cell', open ? 'matrix__cell--open' : '', row2 ? 'matrix__row2' : '']
    .filter(Boolean)
    .join(' ')

  if (!exchange || !raw) {
    return (
      <div className={className}>
        <span className="matrix__empty">{WAITING[field]}</span>
      </div>
    )
  }

  const text = prettyBody(raw, exchange.contentType)
  const runs = splitByValues(text, exchange.entities, tone === 'token')

  return (
    <div className={className}>
      {runs.map((run, index) =>
        run.entity ? (
          <Chip
            key={index}
            entity={run.entity}
            text={run.text}
            tone={tone}
            hot={hoveredToken === run.entity.token}
          />
        ) : (
          <span key={index}>{run.text}</span>
        ),
      )}
    </div>
  )
})

interface ChipProps {
  entity: Entity
  text: string
  tone: 'real' | 'token'
  hot: boolean
}

function Chip({ entity, text, tone, hot }: ChipProps) {
  const hover = () => {
    store.hover(entity.token)
  }
  const leave = () => {
    store.hover(null)
  }
  return (
    <span
      className={`chip chip--${tone}`}
      data-hot={hot}
      onMouseEnter={hover}
      onMouseLeave={leave}
      title={`${entity.kind} · ${Math.round(entity.confidence * 100)}% confidence`}
    >
      {text}
    </span>
  )
}
