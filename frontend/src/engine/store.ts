import {
  PROTOCOL_VERSION,
  type Entity,
  type ExchangeTiming,
  type NearMiss,
  type ProxyPolicy,
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
  /** Mean confidence over the entities; absent until detection, or when there were none. */
  riskScoreMean?: number
  typeFrequencies: Record<string, number>
  /** Findings reported but not replaced on their own. */
  suppressed: number
  nearMisses: NearMiss[]
  /** How many stand-ins were put back in the response. */
  restored?: number
  timing?: ExchangeTiming
  /** The re-identification verdict. Absent until it arrives. */
  privacy?: PrivacyVerdict
}

export interface PrivacyVerdict {
  status: 'ok' | 'skipped' | 'failed'
  risks: { token: string; probability: number }[]
  maxProbability: number
  assessedMs: number
  reason?: string
}

export interface VaultRecord {
  token: string
  kind: string
  value: string
  firstSeenAt: number
  uses: number
  informationType: string
  riskLevel: number
  hipaaCategory: string
}

export interface ProxyInfo {
  name: string
  region: string
  mode: string
  policy: string
}

export interface LogLine {
  seq: number
  at: number
  level: 'info' | 'warn' | 'block'
  message: string
  exchangeId?: string
}

/** What one device behind the proxy has done so far. */
export interface DeviceStats {
  clientLabel: string
  clientIp: string
  seen: number
  treated: number
  identifiers: number
  /** Highest risk level among the identifiers it sent; 0 when none or unknown. */
  maxRisk: number
  lastSeenAt: number
}

interface Metrics {
  requests: number
  treated: number
  identifiersHeld: number
  blocks: number
  latencies: number[]
}

export interface AppState {
  link: LinkStatus
  proxy: ProxyInfo | null
  /** What the proxy said it does, in `hello`. Null until it has. */
  policy: ProxyPolicy | null
  /** What the proxy announced in `hello`. Null until it has. */
  protocolVersion: number | null
  traffic: TrafficEntry[]
  exchanges: Exchange[]
  vault: VaultRecord[]
  metrics: Metrics
  devices: DeviceStats[]
  /** What the proxy said, newest first. */
  logs: LogLine[]
  /** Exchange shown in the inspector. Null means "follow the newest". */
  pinnedId: string | null
  /** Entity the person is pointing at, highlighted everywhere at once. */
  hoveredToken: string | null
  /** Device tile the person is pointing at; its rows light up in the traffic list. */
  hoveredDevice: string | null
}

export const LIMITS = {
  traffic: 140,
  exchanges: 12,
  vault: 40,
  latencies: 60,
  logs: 80,
  devices: 12,
} as const

export const initialState: AppState = {
  link: { state: 'idle', endpoint: '—' },
  proxy: null,
  policy: null,
  protocolVersion: null,
  traffic: [],
  exchanges: [],
  vault: [],
  metrics: { requests: 0, treated: 0, identifiersHeld: 0, blocks: 0, latencies: [] },
  devices: [],
  logs: [],
  pinnedId: null,
  hoveredToken: null,
  hoveredDevice: null,
}

/** Actions the view raises itself. Protocol events are the other kind of input. */
type ViewAction =
  | { type: 'view.settle'; exchangeId: string; at: number }
  | { type: 'view.pin'; exchangeId: string | null }
  | { type: 'view.hover'; token: string | null }
  | { type: 'view.hoverDevice'; clientLabel: string | null }
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
  hoverDevice: (clientLabel: string | null) => void
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
  let logSeq = 0
  const listeners = new Set<() => void>()

  const commit = (next: AppState) => {
    if (next === state) return
    state = next
    for (const listener of listeners) listener()
  }

  const dispatch = (action: Action) => {
    if (action.type === 'request.observed') trafficSeq += 1
    if (action.type === 'log') logSeq += 1
    commit(reduce(state, action, trafficSeq, logSeq))
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
    hoverDevice: (clientLabel) => {
      dispatch({ type: 'view.hoverDevice', clientLabel })
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

/** Replace fields on one exchange without moving it along the band. */
function patchExchangeFields(
  exchanges: Exchange[],
  id: string,
  change: Partial<Exchange>,
): Exchange[] {
  return exchanges.map((exchange) => (exchange.id === id ? { ...exchange, ...change } : exchange))
}

/** One line per device, newest activity first, capped. */
function touchDevice(
  devices: DeviceStats[],
  clientLabel: string,
  clientIp: string,
  at: number,
  change: (device: DeviceStats) => DeviceStats,
): DeviceStats[] {
  const existing = devices.find((device) => device.clientLabel === clientLabel)
  const base: DeviceStats = existing ?? {
    clientLabel,
    clientIp,
    seen: 0,
    treated: 0,
    identifiers: 0,
    maxRisk: 0,
    lastSeenAt: at,
  }
  const next = change({
    ...base,
    clientIp: clientIp || base.clientIp,
    lastSeenAt: Math.max(base.lastSeenAt, at),
  })
  return [next, ...devices.filter((device) => device.clientLabel !== clientLabel)].slice(
    0,
    LIMITS.devices,
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
        informationType: entity.informationType,
        riskLevel: entity.riskLevel,
        hipaaCategory: entity.hipaaCategory,
      })
    }
  }
  return [...fresh, ...next].slice(0, LIMITS.vault)
}

/**
 * Pure: state and an input to the next state. `trafficSeq` is the sequence
 * number to stamp on a new traffic entry; the store counts it.
 */
export function reduce(current: AppState, action: Action, trafficSeq = 0, logSeq = 0): AppState {
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

    case 'view.hoverDevice':
      return current.hoveredDevice === action.clientLabel
        ? current
        : { ...current, hoveredDevice: action.clientLabel }

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
        policy: action.policy,
        protocolVersion: action.version,
        logs: pushLog(current.logs, {
          seq: logSeq,
          at: Date.now(),
          level: action.version === PROTOCOL_VERSION ? 'info' : 'warn',
          message:
            action.version === PROTOCOL_VERSION
              ? `${action.proxy.name} attached · ${action.proxy.mode} · policy ${action.proxy.policy}`
              : `${action.proxy.name} speaks protocol v${action.version}, this view expects v${PROTOCOL_VERSION}`,
        }),
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
        devices: touchDevice(
          current.devices,
          action.clientLabel,
          action.clientIp,
          action.at,
          (device) => ({
            ...device,
            seen: device.seen + 1,
            treated: device.treated + (action.treatment === 'treated' ? 1 : 0),
          }),
        ),
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
        typeFrequencies: {},
        suppressed: 0,
        nearMisses: [],
      }
      return {
        ...current,
        exchanges: evictExchanges([exchange, ...current.exchanges]),
      }
    }

    case 'detection.completed': {
      const exchange = current.exchanges.find((item) => item.id === action.exchangeId)
      const maxRisk = action.entities.reduce((max, entity) => Math.max(max, entity.riskLevel), 0)
      return {
        ...current,
        exchanges: patchExchange(current.exchanges, action.exchangeId, 'inspect', action.at, {
          entities: action.entities,
          scannedMs: action.scannedMs,
          riskScoreMean: action.riskScoreMean,
          typeFrequencies: action.typeFrequencies,
          suppressed: action.suppressed,
          nearMisses: action.nearMisses,
        }),
        traffic: patchTraffic(current.traffic, (entry) => entry.exchangeId === action.exchangeId, {
          identifiers: action.entities.length,
        }),
        vault: mergeVault(current.vault, action.entities, action.at),
        metrics: {
          ...current.metrics,
          identifiersHeld: current.metrics.identifiersHeld + action.entities.length,
        },
        devices: exchange
          ? touchDevice(current.devices, exchange.clientLabel, '', action.at, (device) => ({
              ...device,
              identifiers: device.identifiers + action.entities.length,
              maxRisk: Math.max(device.maxRisk, maxRisk),
            }))
          : current.devices,
      }
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
          restored: action.restored,
        }),
      }

    case 'exchange.delivered':
      return {
        ...current,
        exchanges: patchExchange(current.exchanges, action.exchangeId, 'deliver', action.at, {
          totalMs: action.totalMs,
          timing: action.timing,
        }),
        metrics: {
          ...current.metrics,
          latencies: [...current.metrics.latencies, action.totalMs].slice(-LIMITS.latencies),
        },
      }

    // Arrives late, after the packet has left the band: the stage stays where it is.
    case 'privacy.assessed':
      return current.exchanges.some((exchange) => exchange.id === action.exchangeId)
        ? {
            ...current,
            exchanges: patchExchangeFields(current.exchanges, action.exchangeId, {
              privacy: {
                status: action.status,
                risks: action.risks,
                maxProbability: action.maxProbability,
                assessedMs: action.assessedMs,
                reason: action.reason,
              },
            }),
          }
        : current

    case 'log':
      return {
        ...current,
        logs: pushLog(current.logs, {
          seq: logSeq,
          at: action.at,
          level: action.level,
          message: action.message,
          exchangeId: action.exchangeId,
        }),
        metrics:
          action.level === 'block'
            ? { ...current.metrics, blocks: current.metrics.blocks + 1 }
            : current.metrics,
      }
  }
}

function pushLog(logs: LogLine[], line: LogLine): LogLine[] {
  return [line, ...logs].slice(0, LIMITS.logs)
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
