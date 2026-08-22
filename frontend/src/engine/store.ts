import {
  PROTOCOL_VERSION,
  type Entity,
  type ServerEvent,
  type Treatment,
} from '../protocol/types'
import type { LinkStatus } from '../transport/types'

/**
 * Where a treated exchange stands. Each stage is one position on the band.
 *
 * Most transitions come straight from protocol events. Two are driven by the
 * animation instead, because the proxy has no reason to know where a packet
 * is drawn: `egress` becomes `thinking` once the tokenized request has left
 * the gate, and `deliver` becomes `done` once the restored response has
 * reached the client. Both go through `settle`.
 */
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

/** Where `settle` moves an exchange to, once the view has finished drawing the stage. */
export const SETTLES: Partial<Record<Stage, Stage>> = {
  egress: 'thinking',
  deliver: 'done',
}

/** One line in the traffic list: every request, treated or not. */
export interface TrafficEntry {
  /** Monotonic per store. Lets a view find "everything since I last looked" without a set of ids. */
  seq: number
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
  kind: string
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

interface Metrics {
  requests: number
  treated: number
  identifiersHeld: number
  latencies: number[]
}

export interface AppState {
  link: LinkStatus
  proxy: ProxyInfo | null
  /** What the proxy announced in `hello`. Null until it has. */
  protocolVersion: number | null
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

export const LIMITS = {
  traffic: 140,
  exchanges: 12,
  vault: 40,
  latencies: 60,
} as const

export const initialState: AppState = {
  link: { state: 'idle', endpoint: '—' },
  proxy: null,
  protocolVersion: null,
  traffic: [],
  exchanges: [],
  vault: [],
  metrics: { requests: 0, treated: 0, identifiersHeld: 0, latencies: [] },
  lastLog: null,
  pinnedId: null,
  hoveredToken: null,
}

/** Actions the view raises itself. Protocol events are the other kind of input. */
type ViewAction =
  | { type: 'view.settle'; exchangeId: string; at: number }
  | { type: 'view.pin'; exchangeId: string | null }
  | { type: 'view.hover'; token: string | null }
  | { type: 'view.link'; link: LinkStatus }
  | { type: 'view.reset' }

export type Action = ServerEvent | ViewAction

export interface Store {
  subscribe: (listener: () => void) => () => void
  getSnapshot: () => AppState
  dispatch: (action: Action) => void
  apply: (event: ServerEvent) => void
  setLink: (link: LinkStatus) => void
  pin: (exchangeId: string | null) => void
  hover: (token: string | null) => void
  /** Finish an animation-driven stage: egress → thinking, deliver → done. */
  settle: (exchangeId: string, at?: number) => void
  reset: () => void
}

/**
 * One store per dashboard. The app uses the module instance below; tests and
 * StrictMode-safe experiments get their own with `createStore`.
 */
export function createStore(seed: AppState = initialState): Store {
  let state = seed
  let trafficSeq = 0
  const listeners = new Set<() => void>()

  const commit = (next: AppState) => {
    if (next === state) return
    state = next
    for (const listener of listeners) listener()
  }

  const dispatch = (action: Action) => {
    if (action.type === 'request.observed') trafficSeq += 1
    commit(reduce(state, action, trafficSeq))
  }

  return {
    subscribe: (listener) => {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },
    getSnapshot: () => state,
    dispatch,
    apply: (event) => {
      dispatch(event)
    },
    setLink: (link) => {
      dispatch({ type: 'view.link', link })
    },
    pin: (exchangeId) => {
      dispatch({ type: 'view.pin', exchangeId })
    },
    hover: (token) => {
      dispatch({ type: 'view.hover', token })
    },
    settle: (exchangeId, at = Date.now()) => {
      dispatch({ type: 'view.settle', exchangeId, at })
    },
    reset: () => {
      dispatch({ type: 'view.reset' })
    },
  }
}

export const store: Store = createStore()

/** Replace one exchange, leaving the rest of the list untouched. */
function patchExchange(
  exchanges: Exchange[],
  id: string,
  stage: Stage,
  at: number,
  change: Partial<Exchange>,
): Exchange[] {
  return exchanges.map((exchange) =>
    exchange.id === id ? { ...exchange, ...change, stage, stageAt: at } : exchange,
  )
}

function patchTraffic(
  traffic: TrafficEntry[],
  match: (entry: TrafficEntry) => boolean,
  change: Partial<TrafficEntry>,
): TrafficEntry[] {
  return traffic.map((entry) => (match(entry) ? { ...entry, ...change } : entry))
}

/**
 * Keep the list under its cap. Finished exchanges go first; an in-flight one
 * is only dropped when nothing finished is left to drop, so a burst of
 * treated requests does not orphan the events still arriving for it.
 */
export function evictExchanges(exchanges: Exchange[], max: number = LIMITS.exchanges): Exchange[] {
  if (exchanges.length <= max) return exchanges
  const kept = [...exchanges]
  while (kept.length > max) {
    let index = -1
    for (let i = kept.length - 1; i >= 0; i -= 1) {
      if (kept[i]?.stage === 'done') {
        index = i
        break
      }
    }
    kept.splice(index === -1 ? kept.length - 1 : index, 1)
  }
  return kept
}

/** Merge detected entities into the vault: one record per token, newest first. */
export function mergeVault(vault: VaultRecord[], entities: Entity[], at: number): VaultRecord[] {
  if (entities.length === 0) return vault
  const index = new Map(vault.map((record, i) => [record.token, i]))
  const next = [...vault]
  const fresh: VaultRecord[] = []
  for (const entity of entities) {
    const at_ = index.get(entity.token)
    if (at_ !== undefined) {
      const record = next[at_]
      if (record) next[at_] = { ...record, uses: record.uses + 1 }
    } else {
      index.set(entity.token, -1)
      fresh.push({
        token: entity.token,
        kind: entity.kind,
        value: entity.value,
        firstSeenAt: at,
        uses: 1,
      })
    }
  }
  return [...fresh, ...next].slice(0, LIMITS.vault)
}

/**
 * Pure: state and an input to the next state. `trafficSeq` is the sequence
 * number to stamp on a new traffic entry; the store counts it.
 */
export function reduce(current: AppState, action: Action, trafficSeq = 0): AppState {
  switch (action.type) {
    case 'view.link':
      return { ...current, link: action.link }

    case 'view.pin':
      return current.pinnedId === action.exchangeId
        ? current
        : { ...current, pinnedId: action.exchangeId }

    case 'view.hover':
      return current.hoveredToken === action.token
        ? current
        : { ...current, hoveredToken: action.token }

    case 'view.reset':
      return { ...initialState, link: current.link }

    case 'view.settle': {
      const exchange = current.exchanges.find((item) => item.id === action.exchangeId)
      const next = exchange && SETTLES[exchange.stage]
      if (!next) return current
      return {
        ...current,
        exchanges: patchExchange(current.exchanges, action.exchangeId, next, action.at, {}),
      }
    }

    case 'hello':
      return {
        ...current,
        proxy: action.proxy,
        protocolVersion: action.version,
        lastLog:
          action.version === PROTOCOL_VERSION
            ? `${action.proxy.name} attached · ${action.proxy.mode} · policy ${action.proxy.policy}`
            : `${action.proxy.name} speaks protocol v${action.version}, this view expects v${PROTOCOL_VERSION}`,
      }

    case 'request.observed': {
      const entry: TrafficEntry = {
        seq: trafficSeq,
        requestId: action.requestId,
        at: action.at,
        clientIp: action.clientIp,
        clientLabel: action.clientLabel,
        method: action.method,
        scheme: action.scheme,
        host: action.host,
        path: action.path,
        contentType: action.contentType,
        requestBytes: action.requestBytes,
        treatment: action.treatment,
        reason: action.reason,
        exchangeId: action.exchangeId,
      }
      return {
        ...current,
        traffic: [entry, ...current.traffic].slice(0, LIMITS.traffic),
        metrics: {
          ...current.metrics,
          requests: current.metrics.requests + 1,
          treated: current.metrics.treated + (action.treatment === 'treated' ? 1 : 0),
        },
      }
    }

    case 'request.completed':
      return {
        ...current,
        traffic: patchTraffic(current.traffic, (entry) => entry.requestId === action.requestId, {
          status: action.status,
          responseBytes: action.responseBytes,
          durationMs: action.durationMs,
        }),
      }

    case 'exchange.opened': {
      const exchange: Exchange = {
        id: action.exchangeId,
        requestId: action.requestId,
        openedAt: action.at,
        stage: 'ingress',
        stageAt: action.at,
        clientLabel: action.clientLabel,
        method: action.method,
        scheme: action.scheme,
        host: action.host,
        path: action.path,
        contentType: action.contentType,
        requestBody: action.requestBody,
        entities: [],
      }
      return {
        ...current,
        exchanges: evictExchanges([exchange, ...current.exchanges]),
      }
    }

    case 'detection.completed':
      return {
        ...current,
        exchanges: patchExchange(current.exchanges, action.exchangeId, 'inspect', action.at, {
          entities: action.entities,
          scannedMs: action.scannedMs,
        }),
        traffic: patchTraffic(current.traffic, (entry) => entry.exchangeId === action.exchangeId, {
          identifiers: action.entities.length,
        }),
        vault: mergeVault(current.vault, action.entities, action.at),
        metrics: {
          ...current.metrics,
          identifiersHeld: current.metrics.identifiersHeld + action.entities.length,
        },
      }

    case 'redaction.completed':
      return {
        ...current,
        exchanges: patchExchange(current.exchanges, action.exchangeId, 'redact', action.at, {
          redactedRequestBody: action.redactedRequestBody,
        }),
      }

    case 'upstream.dispatched':
      return {
        ...current,
        exchanges: patchExchange(current.exchanges, action.exchangeId, 'egress', action.at, {
          target: action.target,
          bytes: action.bytes,
        }),
      }

    case 'upstream.responded':
      return {
        ...current,
        exchanges: patchExchange(current.exchanges, action.exchangeId, 'return', action.at, {
          tokenizedResponseBody: action.tokenizedResponseBody,
          status: action.status,
          upstreamMs: action.upstreamMs,
        }),
      }

    case 'rehydration.completed':
      return {
        ...current,
        exchanges: patchExchange(current.exchanges, action.exchangeId, 'rehydrate', action.at, {
          responseBody: action.responseBody,
        }),
      }

    case 'exchange.delivered':
      return {
        ...current,
        exchanges: patchExchange(current.exchanges, action.exchangeId, 'deliver', action.at, {
          totalMs: action.totalMs,
        }),
        metrics: {
          ...current.metrics,
          latencies: [...current.metrics.latencies, action.totalMs].slice(-LIMITS.latencies),
        },
      }

    case 'log':
      return { ...current, lastLog: action.message }
  }
}

export function median(values: number[]): number | null {
  if (values.length === 0) return null
  const sorted = [...values].sort((a, b) => a - b)
  return sorted[Math.floor(sorted.length / 2)] ?? null
}

/** The newest treated exchange still in motion. Untreated traffic never lands here. */
export function activeExchange(exchanges: Exchange[]): Exchange | null {
  return exchanges.find((exchange) => exchange.stage !== 'done') ?? null
}

/**
 * What the inspector shows: the pinned exchange, else the newest one that has
 * been round-tripped so all four cells hold something, else the newest.
 */
export function shownExchange(exchanges: Exchange[], pinnedId: string | null): Exchange | null {
  return (
    exchanges.find((exchange) => exchange.id === pinnedId) ??
    exchanges.find((exchange) => exchange.responseBody) ??
    exchanges[0] ??
    null
  )
}
