import type { Entity, EntityKind, NearMiss } from '../protocol/types'

/**
 * Demo material: a plausible hour of traffic from one household behind the
 * proxy. Most of it is assets nobody needs to read. A few requests carry a JSON
 * body with names, numbers and addresses in it.
 *
 * Markup: {{KIND|the real text}} marks one identifier in a request body.
 * {{KIND}} in a response body refers back to the first identifier of that kind.
 * Offsets are compiled at runtime so the samples stay readable.
 *
 * All names, numbers, accounts and hosts below are invented.
 */

export interface TreatedScenario {
  clientLabel: string
  clientIp: string
  method: string
  host: string
  path: string
  contentType: string
  status: number
  /** Request body with {{KIND|value}} markers. */
  request: string
  /** Response body, written against the tokens the proxy hands out. */
  response: string
  /** Findings under the threshold, for the ones worth showing. */
  nearMisses?: NearMiss[]
  /** Findings nested inside another, which the proxy counts but does not replace. */
  suppressed?: number
}

export interface PlainRequest {
  clientLabel: string
  clientIp: string
  method: string
  host: string
  path: string
  contentType: string
  bytes: number
  responseBytes: number
  status: number
  /** Why the proxy did not treat it. */
  reason: string
}

const TABLET = { clientLabel: 'Tablet · Studer', clientIp: '192.168.1.44' }
const PHONE = { clientLabel: 'Phone · Baumgartner', clientIp: '192.168.1.51' }
const LAPTOP = { clientLabel: 'Laptop · Wyttenbach', clientIp: '192.168.1.23' }

export const TREATED: TreatedScenario[] = [
  {
    ...TABLET,
    method: 'POST',
    host: 'api.helsana-app.ch',
    path: '/v1/claims',
    contentType: 'application/json',
    status: 201,
    request: `{"insuredName":"{{PERSON|Rosmarie Studer}}","ahv":"{{AHV|756.1234.5678.97}}","address":"{{ADDRESS|Aarbergergasse 12, 3011 Bern}}","policy":"{{INSURANCE|42-118-9903}}","treatment":"{{HEALTH|Hüftoperation}}","amountChf":4820.55,"note":"Rechnung wurde bis heute nicht bezahlt"}`,
    response: `{"claimId":"CLM-88213","status":"received","insuredName":"{{PERSON}}","policy":"{{INSURANCE}}","estimatedDays":14}`,
    nearMisses: [{ kind: 'LOCATION', value: 'Bern', confidence: 0.42 }],
    suppressed: 1,
  },
  {
    ...PHONE,
    method: 'POST',
    host: 'api.assistant.ch',
    path: '/v1/messages',
    contentType: 'application/json',
    status: 200,
    request: `{"sessionId":"sess-8812","message":"Mein Enkel schreibt mir per Mail an {{EMAIL|h.baumgartner@bluewin.ch}} und sagt, er sitze im Ausland fest und brauche 3000 Franken. Ich bin {{PERSON|Heidi Baumgartner}}, {{ADDRESS|Länggassstrasse 44, 3012 Bern}}. Soll ich das Geld schicken?"}`,
    response: `{"reply":"Bitte schicken Sie nichts, {{PERSON}}. Das ist das bekannteste Enkeltrick-Muster: Druck, Ausland, sofort Geld. Antworten Sie nicht auf die Mail an {{EMAIL}}, sondern rufen Sie Ihren Enkel unter der Nummer an, die Sie schon lange kennen.","confidence":0.94}`,
    nearMisses: [
      { kind: 'PERSON', value: 'Enkel', confidence: 0.35 },
      { kind: 'LOCATION', value: 'Ausland', confidence: 0.51 },
    ],
  },
  {
    ...LAPTOP,
    method: 'PUT',
    host: 'api.postfinance.ch',
    path: '/v3/transfers/draft',
    contentType: 'application/json',
    status: 200,
    request: `{"debtor":{"name":"{{PERSON|Ernst Wyttenbach}}","iban":"{{IBAN|CH93 0076 2011 6238 5295 7}}","birthDate":"{{BIRTHDATE|14.03.1946}}"},"amountChf":240.00,"reference":"Doppelbelastung prüfen","contactPhone":"{{PHONE|+41 79 412 33 08}}"}`,
    response: `{"draftId":"TRF-4471","status":"pending_review","debtorName":"{{PERSON}}","iban":"{{IBAN}}","reviewBy":"2026-08-24"}`,
  },
  {
    ...TABLET,
    method: 'POST',
    host: 'api.bern.ch',
    path: '/einwohner/v1/umzug',
    contentType: 'application/json',
    status: 202,
    request: `{"person":{"name":"{{PERSON|Marlies Kübler}}","birthDate":"{{BIRTHDATE|02.11.1951}}","ahv":"{{AHV|756.9812.4471.03}}"},"newAddress":"{{ADDRESS|Bümplizstrasse 91, 3018 Bern}}","phone":"{{PHONE|031 991 20 44}}","movingDate":"2026-09-01"}`,
    response: `{"caseId":"UMZ-2026-3391","status":"eingegangen","person":"{{PERSON}}","newAddress":"{{ADDRESS}}"}`,
  },
  {
    ...LAPTOP,
    method: 'POST',
    host: 'api.medi24.ch',
    path: '/v1/triage',
    contentType: 'application/json',
    status: 200,
    request: `{"patient":{"name":"{{PERSON|Peter Hofstetter}}","birthDate":"{{BIRTHDATE|02.11.1951}}","insurer":"{{INSURANCE|CSS 77-390-1122}}"},"symptoms":"{{HEALTH|Diabetes Typ 2}}, Schwindel am Morgen","question":"Was darf ich noch essen?"}`,
    response: `{"advice":"Bei {{HEALTH}} geht es weniger um Verbote als um Rhythmus, {{PERSON}}. Drei Mahlzeiten, wenig Zucker in Getränken, Vollkorn statt Weissmehl.","urgency":"low","insurer":"{{INSURANCE}}"}`,
    nearMisses: [{ kind: 'MEDICAL_LICENSE', value: 'Schwindel', confidence: 0.33 }],
  },
]

/** Passed on content type alone. The proxy does not open these. */
export const PASSTHROUGH: PlainRequest[] = [
  {
    ...TABLET,
    method: 'GET',
    host: 'cdn.helsana-app.ch',
    path: '/assets/app.9f2c1a.css',
    contentType: 'text/css',
    bytes: 0,
    responseBytes: 41208,
    status: 200,
    reason: 'text/css',
  },
  {
    ...TABLET,
    method: 'GET',
    host: 'fonts.gstatic.com',
    path: '/s/ibmplexsans/v14/zYX9KVElMYYaJe8b.woff2',
    contentType: 'font/woff2',
    bytes: 0,
    responseBytes: 28704,
    status: 200,
    reason: 'font/woff2',
  },
  {
    ...PHONE,
    method: 'GET',
    host: 'cdn.assistant.ch',
    path: '/assets/vendor.8ab1f0.js',
    contentType: 'application/javascript',
    bytes: 0,
    responseBytes: 214881,
    status: 200,
    reason: 'application/javascript',
  },
  {
    ...TABLET,
    method: 'GET',
    host: 'static.bern.ch',
    path: '/img/wappen.svg',
    contentType: 'image/svg+xml',
    bytes: 0,
    responseBytes: 3112,
    status: 200,
    reason: 'image/svg+xml',
  },
  {
    ...LAPTOP,
    method: 'GET',
    host: 'app.postfinance.ch',
    path: '/favicon.ico',
    contentType: 'image/x-icon',
    bytes: 0,
    responseBytes: 5430,
    status: 200,
    reason: 'image/x-icon',
  },
  {
    ...LAPTOP,
    method: 'GET',
    host: 'cdn.medi24.ch',
    path: '/img/hero@2x.webp',
    contentType: 'image/webp',
    bytes: 0,
    responseBytes: 88204,
    status: 200,
    reason: 'image/webp',
  },
  {
    ...TABLET,
    method: 'GET',
    host: 'tiles.swisstopo.ch',
    path: '/1.0.0/pixelkarte/12/2145/1436.jpeg',
    contentType: 'image/jpeg',
    bytes: 0,
    responseBytes: 24118,
    status: 200,
    reason: 'image/jpeg',
  },
  {
    ...PHONE,
    method: 'GET',
    host: 'cdn.assistant.ch',
    path: '/fonts/inter-var.woff2',
    contentType: 'font/woff2',
    bytes: 0,
    responseBytes: 44120,
    status: 200,
    reason: 'font/woff2',
  },
  {
    ...TABLET,
    method: 'GET',
    host: 'cdn.helsana-app.ch',
    path: '/assets/icons.sprite.svg',
    contentType: 'image/svg+xml',
    bytes: 0,
    responseBytes: 12988,
    status: 200,
    reason: 'image/svg+xml',
  },
  {
    ...LAPTOP,
    method: 'GET',
    host: 'app.postfinance.ch',
    path: '/assets/runtime.4d1c.js',
    contentType: 'application/javascript',
    bytes: 0,
    responseBytes: 9204,
    status: 200,
    reason: 'application/javascript',
  },
  {
    ...PHONE,
    method: 'GET',
    host: 'cdn.assistant.ch',
    path: '/assets/theme.dark.css',
    contentType: 'text/css',
    bytes: 0,
    responseBytes: 18440,
    status: 304,
    reason: 'text/css',
  },
  {
    ...TABLET,
    method: 'GET',
    host: 'static.bern.ch',
    path: '/fonts/frutiger-roman.woff2',
    contentType: 'font/woff2',
    bytes: 0,
    responseBytes: 31002,
    status: 200,
    reason: 'font/woff2',
  },
]

/** Bodies the proxy did open and found nothing worth holding back. */
export const CLEAN: PlainRequest[] = [
  {
    ...TABLET,
    method: 'GET',
    host: 'api.sbb.ch',
    path: '/v2/departures?station=Bern',
    contentType: 'application/json',
    bytes: 0,
    responseBytes: 8812,
    status: 200,
    reason: 'no identifiers',
  },
  {
    ...PHONE,
    method: 'POST',
    host: 'api.assistant.ch',
    path: '/v1/telemetry',
    contentType: 'application/json',
    bytes: 148,
    responseBytes: 2,
    status: 204,
    reason: 'no identifiers',
  },
  {
    ...LAPTOP,
    method: 'GET',
    host: 'api.srf.ch',
    path: '/v1/weather?region=espace-mittelland',
    contentType: 'application/json',
    bytes: 0,
    responseBytes: 4410,
    status: 200,
    reason: 'no identifiers',
  },
  {
    ...TABLET,
    method: 'POST',
    host: 'api.helsana-app.ch',
    path: '/v1/session/heartbeat',
    contentType: 'application/json',
    bytes: 96,
    responseBytes: 2,
    status: 204,
    reason: 'no identifiers',
  },
  {
    ...PHONE,
    method: 'GET',
    host: 'api.assistant.ch',
    path: '/v1/models',
    contentType: 'application/json',
    bytes: 0,
    responseBytes: 1204,
    status: 200,
    reason: 'no identifiers',
  },
  {
    ...LAPTOP,
    method: 'GET',
    host: 'api.postfinance.ch',
    path: '/v3/rates/chf',
    contentType: 'application/json',
    bytes: 0,
    responseBytes: 640,
    status: 200,
    reason: 'no identifiers',
  },
]

const TOKEN_PREFIX: Record<EntityKind, string> = {
  PERSON: 'PERSON',
  AHV: 'AHV',
  IBAN: 'IBAN',
  ADDRESS: 'ADDRESS',
  PHONE: 'PHONE',
  EMAIL: 'EMAIL',
  BIRTHDATE: 'DOB',
  HEALTH: 'HEALTH',
  INSURANCE: 'INSURANCE',
}

/**
 * What the detector would say about each kind: its label, how identifying it is
 * (1 not, 2 semi, 3 fully -- after Schwartz & Solove), and whether it is health data.
 */
export const KIND_FACTS: Record<
  EntityKind,
  { informationType: string; riskLevel: number; hipaaCategory: string }
> = {
  PERSON: {
    informationType: 'Full Name',
    riskLevel: 3,
    hipaaCategory: 'Not Protected Health Information',
  },
  AHV: {
    informationType: 'Social Security Number',
    riskLevel: 3,
    hipaaCategory: 'Protected Health Information',
  },
  IBAN: {
    informationType: 'International Banking Account Number',
    riskLevel: 3,
    hipaaCategory: 'Not Protected Health Information',
  },
  ADDRESS: {
    informationType: 'Street Address',
    riskLevel: 2,
    hipaaCategory: 'Not Protected Health Information',
  },
  PHONE: {
    informationType: 'Home Phone Number, Cell Phone Number',
    riskLevel: 2,
    hipaaCategory: 'Not Protected Health Information',
  },
  EMAIL: {
    informationType: 'Email Address',
    riskLevel: 3,
    hipaaCategory: 'Not Protected Health Information',
  },
  BIRTHDATE: {
    informationType: 'Date of Birth',
    riskLevel: 2,
    hipaaCategory: 'Protected Health Information',
  },
  HEALTH: {
    informationType: 'Medical Condition, Treatment',
    riskLevel: 2,
    hipaaCategory: 'Protected Health Information',
  },
  INSURANCE: {
    informationType: 'Health Insurance Number',
    riskLevel: 3,
    hipaaCategory: 'Protected Health Information',
  },
}

const MARKER = /\{\{([A-Z]+)\|([^}]*)\}\}/g

export interface CompiledExchange {
  requestBody: string
  redactedRequestBody: string
  entities: Entity[]
  tokenizedResponseBody: string
  responseBody: string
  riskScoreMean: number | undefined
  typeFrequencies: Record<string, number>
  suppressed: number
  nearMisses: NearMiss[]
}

/** Turn a marked-up scenario into the exact shape the protocol carries. */
export function compileExchange(scenario: TreatedScenario, seq: number): CompiledExchange {
  const entities: Entity[] = []
  const tokenByValue = new Map<string, string>()
  const counters = new Map<EntityKind, number>()

  let requestBody = ''
  let redactedRequestBody = ''
  let cursor = 0

  for (const match of scenario.request.matchAll(MARKER)) {
    const [raw, rawKind = '', value = ''] = match
    const kind = rawKind as EntityKind
    const lead = scenario.request.slice(cursor, match.index)
    requestBody += lead
    redactedRequestBody += lead

    let token = tokenByValue.get(value)
    if (!token) {
      const next = (counters.get(kind) ?? 0) + 1
      counters.set(kind, next)
      token = `[${TOKEN_PREFIX[kind]}_${next}]`
      tokenByValue.set(value, token)
    }

    entities.push({
      id: `e${seq}-${entities.length}`,
      kind,
      value,
      token,
      start: requestBody.length,
      end: requestBody.length + value.length,
      confidence: 0.87 + ((value.length * 7) % 12) / 100,
      ...KIND_FACTS[kind],
    })

    requestBody += value
    redactedRequestBody += token
    cursor = (match.index ?? 0) + raw.length
  }

  const tail = scenario.request.slice(cursor)
  requestBody += tail
  redactedRequestBody += tail

  // The destination answers in tokens; rehydration puts the real values back.
  const tokenizedResponseBody = scenario.response.replace(
    /\{\{([A-Z]+)\}\}/g,
    (_, rawKind: string) => {
      const kind = rawKind as EntityKind
      const first = entities.find((entity) => entity.kind === kind)
      return first ? first.token : `[${TOKEN_PREFIX[kind] ?? rawKind}_1]`
    },
  )

  let responseBody = tokenizedResponseBody
  for (const entity of entities) {
    responseBody = responseBody.split(entity.token).join(entity.value)
  }

  const typeFrequencies: Record<string, number> = {}
  for (const entity of entities)
    typeFrequencies[entity.kind] = (typeFrequencies[entity.kind] ?? 0) + 1

  return {
    requestBody,
    redactedRequestBody,
    entities,
    tokenizedResponseBody,
    responseBody,
    riskScoreMean:
      entities.length === 0
        ? undefined
        : Math.round(
            (entities.reduce((sum, entity) => sum + entity.confidence, 0) / entities.length) * 1000,
          ) / 1000,
    typeFrequencies,
    suppressed: scenario.suppressed ?? 0,
    nearMisses: scenario.nearMisses ?? [],
  }
}
