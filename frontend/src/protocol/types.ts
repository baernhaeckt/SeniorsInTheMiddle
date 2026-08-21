/**
 * Wire protocol between the proxy and this view.
 *
 * The proxy is a transparent HTTP/HTTPS proxy. Every request a client makes
 * passes through it, whatever the destination. Most of that traffic is
 * stylesheets, scripts, fonts and images, and it goes past untouched. A small
 * part carries a body worth reading, usually JSON, and only that part gets the
 * treatment this dashboard shows.
 *
 * The proxy decides everything. The view does not poll, does not inspect a
 * payload itself, and does not report a step the proxy did not send.
 *
 * Every request produces:
 *   request.observed -> request.completed
 *
 * A request the proxy treated additionally produces, between those two:
 *   exchange.opened -> detection.completed -> redaction.completed
 *   -> upstream.dispatched -> upstream.responded
 *   -> rehydration.completed -> exchange.delivered
 */

export const PROTOCOL_VERSION = 2

/** What the proxy decided to do with a request. */
export type Treatment =
  /** Non-sensitive by type: CSS, scripts, fonts, images. The body is never read. */
  | 'passthrough'
  /** Body was read, nothing identifying in it. */
  | 'clean'
  /** Identifiers found and replaced before the request left. */
  | 'treated'

/** Categories the proxy is trained to find. Swiss-specific kinds included. */
export type EntityKind =
  | 'PERSON'
  | 'AHV'
  | 'IBAN'
  | 'ADDRESS'
  | 'PHONE'
  | 'EMAIL'
  | 'BIRTHDATE'
  | 'HEALTH'
  | 'INSURANCE'

/** One piece of personal data found in a payload. */
export interface Entity {
  id: string
  kind: EntityKind
  /** The real text, as the client sent it. Never leaves the proxy. */
  value: string
  /** The stand-in the destination sees, e.g. `[PERSON_1]`. */
  token: string
  /** Character offsets into the raw request body. */
  start: number
  end: number
  /** 0..1, as reported by the detector. */
  confidence: number
}

export interface ServerHello {
  type: 'hello'
  version: number
  proxy: {
    name: string
    region: string
    /** How the proxy is deployed, e.g. `transparent http/https`. */
    mode: string
    policy: string
  }
}

/**
 * Emitted once per request, as soon as the proxy has decided how to handle it.
 * This is the firehose: everything the client asked for, in order.
 */
export interface RequestObserved {
  type: 'request.observed'
  requestId: string
  at: number
  clientIp: string
  /** Human label for the device, e.g. `Tablet · Studer`. */
  clientLabel: string
  method: string
  scheme: 'http' | 'https'
  host: string
  path: string
  contentType?: string
  requestBytes: number
  treatment: Treatment
  /** Why it was handled that way, e.g. `text/css` or `6 identifiers`. */
  reason: string
  /** Set when treatment is `treated`; ties the request to its exchange. */
  exchangeId?: string
}

export interface RequestCompleted {
  type: 'request.completed'
  requestId: string
  at: number
  status: number
  responseBytes: number
  durationMs: number
}

export interface ExchangeOpened {
  type: 'exchange.opened'
  exchangeId: string
  requestId: string
  at: number
  clientLabel: string
  method: string
  scheme: 'http' | 'https'
  host: string
  path: string
  contentType: string
  /** The request body exactly as the client sent it. Usually JSON. */
  requestBody: string
}

export interface DetectionCompleted {
  type: 'detection.completed'
  exchangeId: string
  at: number
  entities: Entity[]
  scannedMs: number
}

export interface RedactionCompleted {
  type: 'redaction.completed'
  exchangeId: string
  at: number
  /** The body with every identifier swapped for its token. */
  redactedRequestBody: string
}

export interface UpstreamDispatched {
  type: 'upstream.dispatched'
  exchangeId: string
  at: number
  /** Where it actually went. Any host, not necessarily a model. */
  target: string
  bytes: number
}

export interface UpstreamResponded {
  type: 'upstream.responded'
  exchangeId: string
  at: number
  status: number
  /** Still tokenized. This is what the destination returned. */
  tokenizedResponseBody: string
  upstreamMs: number
}

export interface RehydrationCompleted {
  type: 'rehydration.completed'
  exchangeId: string
  at: number
  /** Tokens swapped back, for the client's eyes only. */
  responseBody: string
  restored: number
}

export interface ExchangeDelivered {
  type: 'exchange.delivered'
  exchangeId: string
  at: number
  totalMs: number
}

export interface ProxyLog {
  type: 'log'
  at: number
  level: 'info' | 'warn' | 'block'
  message: string
  exchangeId?: string
}

export type ServerEvent =
  | ServerHello
  | RequestObserved
  | RequestCompleted
  | ExchangeOpened
  | DetectionCompleted
  | RedactionCompleted
  | UpstreamDispatched
  | UpstreamResponded
  | RehydrationCompleted
  | ExchangeDelivered
  | ProxyLog

export type ServerEventType = ServerEvent['type']

/** Narrow an unknown WebSocket frame to a protocol event. */
export function parseServerEvent(raw: unknown): ServerEvent | null {
  if (typeof raw !== 'string') return null
  let data: unknown
  try {
    data = JSON.parse(raw)
  } catch {
    return null
  }
  if (!data || typeof data !== 'object') return null
  const type = (data as { type?: unknown }).type
  return typeof type === 'string' ? (data as ServerEvent) : null
}
