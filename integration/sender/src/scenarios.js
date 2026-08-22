/**
 * The kinds of request the harness makes, and why each one is here.
 *
 * The proxy classifies traffic before it decides what to do with it: a stylesheet is
 * passed through without its body ever being read, a JSON payload is read, and only a
 * payload with identifiers in it gets treated. The mix below covers all three, plus the
 * awkward cases that break tunnels rather than parsers -- a chunked response, a slow
 * upstream, an error status, a body larger than one 8 KiB StreamProxy chunk, and a
 * response that is not valid UTF-8.
 *
 * `secrets` are values that must reach the client unchanged. `names` are the person
 * names in the body, declared to the receiver in a header because a name cannot be
 * found by shape the way an AHV number can.
 */
import { person, secretsOf, pick } from './fixtures.js'

const json = (value) => ({
  body: JSON.stringify(value, null, 2),
  headers: { 'content-type': 'application/json; charset=utf-8' },
})

/** Requests with no personal data in them. */
export const CLEAN_SCENARIOS = [
  {
    name: 'asset',
    weight: 50,
    describe: 'Static asset, body never read by the proxy',
    build: (rng) => ({
      method: 'GET',
      path: pick(rng, ['/assets/app.css', '/assets/app.js', '/assets/logo.svg', '/assets/photo.jpg']),
      expect: 200,
    }),
  },
  {
    name: 'status',
    weight: 20,
    describe: 'Small JSON response',
    build: () => ({ method: 'GET', path: '/api/v1/status', expect: 200 }),
  },
  {
    name: 'clean-json',
    weight: 12,
    describe: 'JSON body the proxy reads and finds nothing in',
    build: (rng) => ({
      method: 'POST',
      path: '/api/v1/forms/intake',
      expect: 201,
      ...json({
        form: 'opening-hours',
        question: 'Wann hat die Filiale am Samstag offen?',
        locale: 'de-CH',
        ticket: Math.floor(rng() * 100000),
      }),
    }),
  },
  {
    name: 'chunked',
    weight: 6,
    describe: 'Chunked response, several writes through the tunnel',
    build: () => ({ method: 'GET', path: '/stream/chunks?count=6', expect: 200 }),
  },
  {
    name: 'slow',
    weight: 4,
    describe: 'Slow upstream',
    build: (rng) => ({
      method: 'GET',
      path: `/slow?ms=${500 + Math.floor(rng() * 1500)}`,
      expect: 200,
    }),
  },
  {
    name: 'error',
    weight: 4,
    describe: 'Error status forwarded unchanged',
    build: (rng) => {
      const status = pick(rng, [400, 404, 418, 500, 503])
      return { method: 'GET', path: `/error/${status}`, expect: status }
    },
  },
  {
    name: 'upload',
    weight: 4,
    describe: 'Body larger than one 8 KiB proxy chunk',
    build: (rng) => ({
      method: 'POST',
      path: '/upload',
      expect: 202,
      ...json({
        note: 'Scan des Formulars',
        blob: 'x'.repeat(12000 + Math.floor(rng() * 8000)),
      }),
    }),
  },
]

/** Requests carrying personal data. These are the ones the dashboard exists for. */
export const PII_SCENARIOS = [
  {
    name: 'intake',
    weight: 50,
    describe: 'Form submission with the full set of identifiers',
    build: (rng) => {
      const subject = person(rng)
      return {
        method: 'POST',
        path: '/api/v1/forms/intake',
        expect: 201,
        names: [subject.name],
        secrets: secretsOf(subject),
        ...json({
          form: 'krankenkassen-wechsel',
          applicant: {
            name: subject.name,
            ahv: subject.ahv,
            birthDate: subject.birthDate,
            address: subject.address,
            phone: subject.phone,
            email: subject.email,
          },
          payment: { iban: subject.iban, holder: subject.name },
          insurance: { current: subject.insurer, policyNumber: subject.policyNumber },
        }),
      }
    },
  },
  {
    name: 'chat',
    weight: 40,
    describe: 'Prompt to a model-shaped destination, identifiers inside the text',
    build: (rng) => {
      const subject = person(rng)
      const prompt =
        `Ich heisse ${subject.name}, geboren am ${subject.birthDate}, ` +
        `wohnhaft an der ${subject.address}. Meine AHV-Nummer ist ${subject.ahv} ` +
        `und ich bin bei der ${subject.insurer} versichert. ` +
        `Wegen ${subject.condition} brauche ich eine Kostengutsprache. ` +
        `Erreichbar bin ich unter ${subject.phone} oder ${subject.email}. ` +
        `Die Rückerstattung bitte auf ${subject.iban}.`
      return {
        method: 'POST',
        path: '/api/v1/chat/completions',
        expect: 200,
        names: [subject.name],
        secrets: secretsOf(subject),
        ...json({
          model: 'harness-echo-1',
          messages: [
            { role: 'system', content: 'Du hilfst Versicherten beim Schriftverkehr.' },
            { role: 'user', content: prompt },
          ],
        }),
      }
    },
  },
  {
    name: 'contact',
    weight: 10,
    describe: 'Short payload with a single identifier',
    build: (rng) => {
      const subject = person(rng)
      return {
        method: 'POST',
        path: '/api/v1/forms/intake',
        expect: 201,
        names: [subject.name],
        secrets: [subject.name, subject.phone],
        ...json({ form: 'rueckruf', name: subject.name, phone: subject.phone }),
      }
    },
  },
]

export const ALL_SCENARIOS = [...CLEAN_SCENARIOS, ...PII_SCENARIOS]

export function findScenario(name) {
  return ALL_SCENARIOS.find((scenario) => scenario.name === name)
}

function weightedPick(rng, scenarios) {
  const total = scenarios.reduce((sum, scenario) => sum + scenario.weight, 0)
  let ticket = rng() * total
  for (const scenario of scenarios) {
    ticket -= scenario.weight
    if (ticket <= 0) return scenario
  }
  return scenarios.at(-1)
}

/**
 * Picks the next request. The personal-data, https and tls-to-proxy shares are knobs
 * rather than weights, so the UI can push each to 0 or 1 without rewriting the table.
 *
 * Scheme and transport are drawn independently, so all four combinations occur:
 * absolute-form over plain, absolute-form inside TLS, CONNECT over plain, and CONNECT
 * inside TLS (TLS in TLS).
 */
export function nextRequest(rng, { piiRatio, httpsRatio, proxyTlsRatio, proxyTlsPort }) {
  const carriesPii = rng() < piiRatio
  const scenario = weightedPick(rng, carriesPii ? PII_SCENARIOS : CLEAN_SCENARIOS)
  const scheme = rng() < httpsRatio ? 'https' : 'http'
  // Always drawn, so the sequence for a seed does not depend on whether the TLS
  // listener is configured.
  const proxyTls = rng() < proxyTlsRatio && proxyTlsPort > 0
  return build(scenario, rng, scheme, proxyTls)
}

/**
 * @param scheme    'http' (absolute form) or 'https' (CONNECT + intercept) to the target
 * @param proxyTls  whether the connection to the proxy itself is TLS
 */
export function build(scenario, rng, scheme, proxyTls = false) {
  const spec = scenario.build(rng)
  return {
    scenario: scenario.name,
    describe: scenario.describe,
    carriesPii: PII_SCENARIOS.includes(scenario),
    scheme,
    proxyTls: Boolean(proxyTls),
    method: spec.method,
    path: spec.path,
    headers: spec.headers ?? {},
    body: spec.body ?? '',
    expect: spec.expect,
    names: spec.names ?? [],
    secrets: spec.secrets ?? [],
  }
}
