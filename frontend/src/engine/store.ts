import type { Entity, EntityKind, ServerEvent, Treatment } from '../protocol/types'
import type { LinkStatus } from '../transport/types'

/** Where a treated exchange stands. Each stage is one position on the band. */
export type Stage =
  | 'ingress'
  | 'inspect'
  | 'redact'
  | 'egress'
  | 'thinking'
  | 'return'
  | 'rehydrate'
  | 'deliver'
  | 'done'

/** One line in the traffic list: every request, treated or not. */
export interface TrafficEntry {
  requestId: string
  at: number
  clientIp: string
  clientLabel: string
  method: string
  scheme: 'http' | 'https'
  host: string
  path: string
  contentType?: string
  requestBytes: number
  treatment: Treatment
  reason: string
  exchangeId?: string
  /** Filled in once detection reports back. */
  identifiers?: number
  status?: number
  responseBytes?: number
  durationMs?: number
}

/** A request the proxy opened and rewrote. Only these reach the band. */
export interface Exchange {
  id: string
  requestId: string
  openedAt: number
  stage: Stage
  stageAt: number
  clientLabel: string
  method: string
  scheme: 'http' | 'https'
  host: string
  path: string
  contentType: string
  requestBody: string
  redactedRequestBody?: string
  entities: Entity[]
  tokenizedResponseBody?: string
  responseBody?: string
  target?: string
  status?: number
  bytes?: number
  scannedMs?: number
  upstreamMs?: number
  totalMs?: number
}

export interface VaultRecord {
  token: string
  kind: EntityKind
  value: string
  firstSeenAt: number
  uses: number
}

export interface ProxyInfo {
  name: string
  region: string
  mode: string
  policy: string
}

export interface Metrics {
  requests: number
  treated: number
  identifiersHeld: number
  leaks: number
  latencies: number[]
}

export interface AppState {
  link: LinkStatus
  proxy: ProxyInfo | null
  traffic: TrafficEntry[]
  exchanges: Exchange[]
  vault: VaultRecord[]
  metrics: Metrics
  /** Latest thing the proxy said, shown under the traffic list. */
  lastLog: string | null
  /** Exchange shown in the inspector. Null means "follow the newest". */
  pinnedId: string | null
  /** Entity the person is pointing at, highlighted everywhere at once. */
  hoveredToken: string | null
}

const MAX_TRAFFIC = 140
const MAX_EXCHANGES = 12
const MAX_VAULT = 40

const initialState: AppState = {
  link: { state: 'idle', endpoint: '—' },
  proxy: null,
  traffic: [],
  exchanges: [],
  vault: [],
  metrics: { requests: 0, treated: 0, identifiersHeld: 0, leaks: 0, latencies: [] },
  lastLog: null,
  pinnedId: null,
  hoveredToken: null,
}

let state: AppState = initialState
const listeners = new Set<() => void>()

const commit = (next: AppState) => {
  state = next
  for (const listener of listeners) listener()
}

export const store = {
  subscribe(listener: () => void) {
    listeners.add(listener)
    return () => {
      listeners.delete(listener)
    }
  },
  getSnapshot(): AppState {
    return state
  },
  setLink(link: LinkStatus) {
    commit({ ...state, link })
  },
  pin(id: string | null) {
    commit({ ...state, pinnedId: id })
  },
  hover(token: string | null) {
    if (state.hoveredToken === token) return
    commit({ ...state, hoveredToken: token })
  },
  apply(event: ServerEvent) {
    commit(reduce(state, event))
  },
  reset() {
    commit({ ...initialState, link: state.link })
  },
}

/** Replace one exchange, leaving the rest of the list untouched. */
function patchExchange(
  current: AppState,
  id: string,
  stage: Stage,
  at: number,
  change: Partial<Exchange>,
): Exchange[] {
  return current.exchanges.map((exchange) =>
    exchange.id === id ? { ...exchange, ...change, stage, stageAt: at } : exchange,
  )
}

function patchTraffic(
  current: AppState,
  match: (entry: TrafficEntry) => boolean,
  change: Partial<TrafficEntry>,
): TrafficEntry[] {
  return current.traffic.map((entry) => (match(entry) ? { ...entry, ...change } : entry))
}

export function reduce(current: AppState, event: ServerEvent): AppState {
  switch (event.type) {
    case 'hello':
      return {
        ...current,
        proxy: event.proxy,
        lastLog: `${event.proxy.name} attached · ${event.proxy.mode} · policy ${event.proxy.policy}`,
      }

    case 'request.observed': {
      const entry: TrafficEntry = {
        requestId: event.requestId,
        at: event.at,
        clientIp: event.clientIp,
        clientLabel: event.clientLabel,
        method: event.method,
        scheme: event.scheme,
        host: event.host,
        path: event.path,
        contentType: event.contentType,
        requestBytes: event.requestBytes,
        treatment: event.treatment,
        reason: event.reason,
        exchangeId: event.exchangeId,
      }
      return {
        ...current,
        traffic: [entry, ...current.traffic].slice(0, MAX_TRAFFIC),
        metrics: {
          ...current.metrics,
          requests: current.metrics.requests + 1,
          treated: current.metrics.treated + (event.treatment === 'treated' ? 1 : 0),
        },
      }
    }

    case 'request.completed':
      return {
        ...current,
        traffic: patchTraffic(current, (entry) => entry.requestId === event.requestId, {
          status: event.status,
          responseBytes: event.responseBytes,
          durationMs: event.durationMs,
        }),
      }

    case 'exchange.opened': {
      const exchange: Exchange = {
        id: event.exchangeId,
        requestId: event.requestId,
        openedAt: event.at,
        stage: 'ingress',
        stageAt: event.at,
        clientLabel: event.clientLabel,
        method: event.method,
        scheme: event.scheme,
        host: event.host,
        path: event.path,
        contentType: event.contentType,
        requestBody: event.requestBody,
        entities: [],
      }
      return {
        ...current,
        exchanges: [exchange, ...current.exchanges].slice(0, MAX_EXCHANGES),
      }
    }

    case 'detection.completed': {
      const vault = current.vault.map((record) => ({ ...record }))
      for (const entity of event.entities) {
        const existing = vault.find((record) => record.token === entity.token)
        if (existing) {
          existing.uses += 1
        } else {
          vault.unshift({
            token: entity.token,
            kind: entity.kind,
            value: entity.value,
            firstSeenAt: event.at,
            uses: 1,
          })
        }
      }
      return {
        ...current,
        exchanges: patchExchange(current, event.exchangeId, 'inspect', event.at, {
          entities: event.entities,
          scannedMs: event.scannedMs,
        }),
        traffic: patchTraffic(current, (entry) => entry.exchangeId === event.exchangeId, {
          identifiers: event.entities.length,
        }),
        vault: vault.slice(0, MAX_VAULT),
        metrics: {
          ...current.metrics,
          identifiersHeld: current.metrics.identifiersHeld + event.entities.length,
        },
      }
    }

    case 'redaction.completed':
      return {
        ...current,
        exchanges: patchExchange(current, event.exchangeId, 'redact', event.at, {
          redactedRequestBody: event.redactedRequestBody,
        }),
      }

    case 'upstream.dispatched':
      return {
        ...current,
        exchanges: patchExchange(current, event.exchangeId, 'thinking', event.at, {
          target: event.target,
          bytes: event.bytes,
        }),
      }

    case 'upstream.responded':
      return {
        ...current,
        exchanges: patchExchange(current, event.exchangeId, 'return', event.at, {
          tokenizedResponseBody: event.tokenizedResponseBody,
          status: event.status,
          upstreamMs: event.upstreamMs,
        }),
      }

    case 'rehydration.completed':
      return {
        ...current,
        exchanges: patchExchange(current, event.exchangeId, 'rehydrate', event.at, {
          responseBody: event.responseBody,
        }),
      }

    case 'exchange.delivered':
      return {
        ...current,
        exchanges: patchExchange(current, event.exchangeId, 'deliver', event.at, {
          totalMs: event.totalMs,
        }),
        metrics: {
          ...current.metrics,
          latencies: [...current.metrics.latencies, event.totalMs].slice(-60),
        },
      }

    case 'log':
      return { ...current, lastLog: event.message }

    default:
      return current
  }
}

/** Move a delivered exchange off the band once its packet has landed. */
export function retireExchange(id: string) {
  const exchange = state.exchanges.find((item) => item.id === id)
  if (!exchange || exchange.stage !== 'deliver') return
  commit({
    ...state,
    exchanges: state.exchanges.map((item) =>
      item.id === id ? { ...item, stage: 'done' as Stage, stageAt: Date.now() } : item,
    ),
  })
}

export function median(values: number[]): number | null {
  if (values.length === 0) return null
  const sorted = [...values].sort((a, b) => a - b)
  return sorted[Math.floor(sorted.length / 2)]
}
