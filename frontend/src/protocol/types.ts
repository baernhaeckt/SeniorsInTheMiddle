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
 *
 * The schemas below are the contract. The TypeScript types are derived from
 * them, so a frame that type-checks is also one that validates at runtime.
 * Recorded frames live in `./fixtures`; every one of them must parse.
 */

import * as v from 'valibot'

export const PROTOCOL_VERSION = 3

/** What the proxy decided to do with a request. */
export const TreatmentSchema = v.picklist([
  /** Non-sensitive by type: CSS, scripts, fonts, images. The body is never read. */
  'passthrough',
  /** Body was read, nothing identifying in it. */
  'clean',
  /** Identifiers found and replaced before the request left. */
  'treated',
])
export type Treatment = v.InferOutput<typeof TreatmentSchema>

/**
 * The kinds the demo feed produces. On the wire `kind` is whatever name the proxy's detector
 * gives a finding (`PERSON`, `EMAIL_ADDRESS`, `IBAN_CODE`, ...) and is shown as it arrives,
 * so the protocol accepts any non-empty string; this list is for the demo only.
 */
export const EntityKindSchema = v.picklist([
  'PERSON',
  'AHV',
  'IBAN',
  'ADDRESS',
  'PHONE',
  'EMAIL',
  'BIRTHDATE',
  'HEALTH',
  'INSURANCE',
])
export type EntityKind = v.InferOutput<typeof EntityKindSchema>

const SchemeSchema = v.picklist(['http', 'https'])
const Millis = v.pipe(v.number(), v.minValue(0))
const Epoch = v.pipe(v.number(), v.minValue(0))
const Id = v.pipe(v.string(), v.minLength(1))

/** One piece of personal data found in a payload. */
export const EntitySchema = v.object({
  id: Id,
  /** The detector's own name for the category, e.g. `PERSON` or `EMAIL_ADDRESS`. */
  kind: v.pipe(v.string(), v.minLength(1)),
  /** The real text, as the client sent it. Never leaves the proxy. */
  value: v.string(),
  /** The stand-in the destination sees, e.g. `[PERSON_1]`. */
  token: v.pipe(v.string(), v.minLength(1)),
  /** Character offsets into the raw request body. */
  start: v.pipe(v.number(), v.minValue(0)),
  end: v.pipe(v.number(), v.minValue(0)),
  /** 0..1, as reported by the detector. */
  confidence: v.pipe(v.number(), v.minValue(0), v.maxValue(1)),
  /** The detector's human-readable label for the kind, e.g. `Full Name`. Empty when it has none. */
  informationType: v.optional(v.string(), ''),
  /** 1..3 after Schwartz & Solove: not, semi, fully identifiable. 0 when the detector did not say. */
  riskLevel: v.optional(v.pipe(v.number(), v.integer(), v.minValue(0), v.maxValue(3)), 0),
  /** Whether the kind is protected health information, in the detector's words. */
  hipaaCategory: v.optional(v.string(), ''),
})
export type Entity = v.InferOutput<typeof EntitySchema>

/** A finding below the confidence threshold. Reported, never replaced. No offsets. */
export const NearMissSchema = v.object({
  kind: v.pipe(v.string(), v.minLength(1)),
  value: v.string(),
  confidence: v.pipe(v.number(), v.minValue(0), v.maxValue(1)),
})
export type NearMiss = v.InferOutput<typeof NearMissSchema>

export const ServiceStateSchema = v.picklist(['ok', 'disabled', 'down'])
export type ServiceState = v.InferOutput<typeof ServiceStateSchema>

/** What the proxy does and does not look at. Announced once, in `hello`. */
export const ProxyPolicySchema = v.object({
  /** True when bodies are rewritten; false when the proxy only watches. */
  rewrite: v.boolean(),
  /** Hosts tunnelled unread. */
  bypassHosts: v.array(v.string()),
  /** Hosts on which only the listed paths are inspected. */
  inspectOnly: v.record(v.string(), v.array(v.string())),
  /** Largest body offered to the rewrite. */
  maxBodyBytes: v.pipe(v.number(), v.minValue(0)),
  /** The detector's score below which a finding is a near miss. */
  confidenceThreshold: v.pipe(v.number(), v.minValue(0), v.maxValue(1)),
  services: v.object({
    pii: ServiceStateSchema,
    privacyCheck: ServiceStateSchema,
  }),
})
export type ProxyPolicy = v.InferOutput<typeof ProxyPolicySchema>

export const ServerHelloSchema = v.object({
  type: v.literal('hello'),
  version: v.number(),
  proxy: v.object({
    name: v.string(),
    region: v.string(),
    /** How the proxy is deployed, e.g. `transparent http/https`. */
    mode: v.string(),
    policy: v.string(),
  }),
  policy: ProxyPolicySchema,
})

/**
 * Emitted once per request, as soon as the proxy has decided how to handle it.
 * This is the firehose: everything the client asked for, in order.
 */
export const RequestObservedSchema = v.object({
  type: v.literal('request.observed'),
  requestId: Id,
  at: Epoch,
  clientIp: v.string(),
  /** Human label for the device, e.g. `Tablet · Studer`. */
  clientLabel: v.string(),
  method: v.string(),
  scheme: SchemeSchema,
  host: v.string(),
  path: v.string(),
  contentType: v.optional(v.string()),
  requestBytes: v.pipe(v.number(), v.minValue(0)),
  treatment: TreatmentSchema,
  /** Why it was handled that way, e.g. `text/css` or `6 identifiers`. */
  reason: v.string(),
  /** Set when treatment is `treated`; ties the request to its exchange. */
  exchangeId: v.optional(Id),
})

export const RequestCompletedSchema = v.object({
  type: v.literal('request.completed'),
  requestId: Id,
  at: Epoch,
  status: v.pipe(v.number(), v.integer()),
  responseBytes: v.pipe(v.number(), v.minValue(0)),
  durationMs: Millis,
})

export const ExchangeOpenedSchema = v.object({
  type: v.literal('exchange.opened'),
  exchangeId: Id,
  requestId: Id,
  at: Epoch,
  clientLabel: v.string(),
  method: v.string(),
  scheme: SchemeSchema,
  host: v.string(),
  path: v.string(),
  contentType: v.string(),
  /** The request body exactly as the client sent it. Usually JSON. */
  requestBody: v.string(),
})

export const DetectionCompletedSchema = v.object({
  type: v.literal('detection.completed'),
  exchangeId: Id,
  at: Epoch,
  entities: v.array(EntitySchema),
  scannedMs: Millis,
  /** Mean confidence over the entities. Absent when there are none. */
  riskScoreMean: v.optional(v.pipe(v.number(), v.minValue(0), v.maxValue(1))),
  /** How many of each kind were replaced. */
  typeFrequencies: v.optional(v.record(v.string(), v.pipe(v.number(), v.minValue(0))), {}),
  /** Findings reported but not replaced on their own: nested in another, or unplaceable. */
  suppressed: v.optional(v.pipe(v.number(), v.integer(), v.minValue(0)), 0),
  nearMisses: v.optional(v.array(NearMissSchema), []),
})

export const RedactionCompletedSchema = v.object({
  type: v.literal('redaction.completed'),
  exchangeId: Id,
  at: Epoch,
  /** The body with every identifier swapped for its token. */
  redactedRequestBody: v.string(),
})

export const UpstreamDispatchedSchema = v.object({
  type: v.literal('upstream.dispatched'),
  exchangeId: Id,
  at: Epoch,
  /** Where it actually went. Any host, not necessarily a model. */
  target: v.string(),
  bytes: v.pipe(v.number(), v.minValue(0)),
})

export const UpstreamRespondedSchema = v.object({
  type: v.literal('upstream.responded'),
  exchangeId: Id,
  at: Epoch,
  status: v.pipe(v.number(), v.integer()),
  /** Still tokenized. This is what the destination returned. */
  tokenizedResponseBody: v.string(),
  upstreamMs: Millis,
})

export const RehydrationCompletedSchema = v.object({
  type: v.literal('rehydration.completed'),
  exchangeId: Id,
  at: Epoch,
  /** Tokens swapped back, for the client's eyes only. */
  responseBody: v.string(),
  restored: v.pipe(v.number(), v.minValue(0)),
})

/** Where the round trip went. Steps the exchange never reached are 0. */
export const ExchangeTimingSchema = v.object({
  bufferMs: Millis,
  detectMs: Millis,
  upstreamMs: Millis,
  rehydrateMs: Millis,
  overheadMs: Millis,
})
export type ExchangeTiming = v.InferOutput<typeof ExchangeTimingSchema>

export const ExchangeDeliveredSchema = v.object({
  type: v.literal('exchange.delivered'),
  exchangeId: Id,
  at: Epoch,
  totalMs: Millis,
  timing: v.optional(ExchangeTimingSchema),
})

/**
 * How recoverable the replaced names still are from the redacted text. Runs off the
 * request path and arrives late, usually after `exchange.delivered`; a status other
 * than `ok` says why there is no number.
 */
export const PrivacyAssessedSchema = v.object({
  type: v.literal('privacy.assessed'),
  exchangeId: Id,
  at: Epoch,
  risks: v.array(
    v.object({
      token: v.pipe(v.string(), v.minLength(1)),
      probability: v.pipe(v.number(), v.minValue(0), v.maxValue(1)),
    }),
  ),
  maxProbability: v.pipe(v.number(), v.minValue(0), v.maxValue(1)),
  assessedMs: Millis,
  status: v.picklist(['ok', 'skipped', 'failed']),
  reason: v.optional(v.string()),
})

export const ProxyLogSchema = v.object({
  type: v.literal('log'),
  at: Epoch,
  level: v.picklist(['info', 'warn', 'block']),
  message: v.string(),
  exchangeId: v.optional(Id),
})

export const ServerEventSchema = v.variant('type', [
  ServerHelloSchema,
  RequestObservedSchema,
  RequestCompletedSchema,
  ExchangeOpenedSchema,
  DetectionCompletedSchema,
  RedactionCompletedSchema,
  UpstreamDispatchedSchema,
  UpstreamRespondedSchema,
  RehydrationCompletedSchema,
  ExchangeDeliveredSchema,
  PrivacyAssessedSchema,
  ProxyLogSchema,
])
export type ServerEvent = v.InferOutput<typeof ServerEventSchema>
type ServerEventType = ServerEvent['type']
/** One event by its `type`, e.g. `EventOf<'hello'>`. */
export type EventOf<T extends ServerEventType> = Extract<ServerEvent, { type: T }>

export type ParseResult =
  | { ok: true; event: ServerEvent }
  | { ok: false; reason: 'not-text' | 'not-json' | 'not-an-object' | 'invalid'; detail: string }

/**
 * Narrow an unknown WebSocket frame to a protocol event.
 *
 * Anything that is not valid against the schema is rejected with a reason, so
 * the transport can count it and the header can say frames are being dropped.
 * A frame with an unknown `type` is rejected too: the view does not guess.
 */
export function parseServerEvent(raw: unknown): ParseResult {
  if (typeof raw !== 'string') return { ok: false, reason: 'not-text', detail: typeof raw }

  let data: unknown
  try {
    data = JSON.parse(raw)
  } catch (error) {
    return { ok: false, reason: 'not-json', detail: messageOf(error) }
  }
  if (!data || typeof data !== 'object') {
    return { ok: false, reason: 'not-an-object', detail: typeof data }
  }

  const result = v.safeParse(ServerEventSchema, data)
  if (result.success) return { ok: true, event: result.output }

  const type = (data as { type?: unknown }).type
  const first = result.issues[0]
  const path = first.path?.map((segment) => String(segment.key)).join('.') ?? ''
  return {
    ok: false,
    reason: 'invalid',
    detail: `${typeof type === 'string' ? type : 'unknown type'}${path ? ` at ${path}` : ''}: ${first.message}`,
  }
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
