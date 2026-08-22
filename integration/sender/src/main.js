/**
 * The sender: a device that has been handed a proxy address and told to use it.
 *
 * It reaches the receiver only through the proxy, generates a randomised but repeatable
 * mix of traffic, and keeps going until it is stopped. Two things make each exchange
 * worth recording:
 *
 *   - what the client got back, which is what a person would notice, and
 *   - what the receiver saw, which is what a person would never notice.
 *
 * The gap between those two is the product. This process measures one side of it and
 * reads the other side's counters over the network.
 */
import { setTimeout as delay } from 'node:timers/promises'

import { settings, publicSettings } from './config.js'
import { createRng } from './prng.js'
import { nextRequest, build } from './scenarios.js'
import { send } from './proxyClient.js'
import { waitForCa } from './ca.js'
import { createStore } from './store.js'
import { startUi } from './ui.js'

const TOKEN_PATTERN = /\[(PERSON|AHV|IBAN|ADDRESS|PHONE|EMAIL|BIRTHDATE|HEALTH|INSURANCE)_\d+\]/

const store = createStore({ historySize: settings.historySize })
const rng = createRng(settings.seed)

let ca = null
let stopping = false
let completed = 0

const log = (event, fields = {}) =>
  console.log(JSON.stringify({ at: new Date().toISOString(), event, ...fields }))

function preview(text) {
  if (!text) return ''
  return text.length > settings.previewBytes ? `${text.slice(0, settings.previewBytes)}\n…truncated…` : text
}

/** Sends one request and files the result. */
async function execute(spec) {
  const port = spec.scheme === 'https' ? settings.targetHttpsPort : settings.targetHttpPort
  const result = await send(spec, {
    proxyHost: settings.proxyHost,
    proxyPort: settings.proxyPort,
    targetHost: settings.targetHost,
    targetHttpPort: settings.targetHttpPort,
    targetHttpsPort: settings.targetHttpsPort,
    timeoutMs: settings.requestTimeoutMs,
    caPem: ca?.pem,
  })

  // Did every value the client sent come back to the client? With redaction and
  // rehydration in place this stays true while the receiver stops seeing any of them.
  const missing = result.ok ? spec.secrets.filter((secret) => !result.responseBody.includes(secret)) : []

  const record = store.add({
    at: Date.now(),
    scenario: spec.scenario,
    describe: spec.describe,
    scheme: spec.scheme,
    method: spec.method,
    url: `${spec.scheme}://${settings.targetHost}:${port}${spec.path}`,
    path: spec.path,
    status: result.status,
    ok: result.ok,
    expected: spec.expect ?? null,
    durationMs: Number(result.durationMs.toFixed(1)),
    requestBytes: Buffer.byteLength(spec.body ?? ''),
    responseBytes: result.responseBytes,
    error: result.error,
    tls: result.tls,
    piiSent: spec.carriesPii,
    piiIntact: spec.carriesPii && result.ok ? missing.length === 0 : null,
    missingSecrets: missing.length,
    secretCount: spec.secrets.length,
    tokensInResponse: TOKEN_PATTERN.test(result.responseBody),
    requestPreview: preview(spec.body),
    responsePreview: preview(result.responseBody),
  })

  completed += 1
  if (record.error) await refreshCaIfStale(record.error)
  if (!record.ok) {
    log('request-failed', {
      seq: record.seq,
      scenario: record.scenario,
      scheme: record.scheme,
      status: record.status,
      expected: record.expected,
      error: record.error,
    })
  }
  return record
}

const STALE_CA_PATTERN = /verify the first certificate|self[- ]signed|unknown ca|certificate/i
const CA_REFRESH_INTERVAL_MS = 30_000
let lastCaRefresh = 0

/**
 * A proxy that is recreated without its /app/certs volume mints a new CA, and every
 * HTTPS request then fails against the one fetched at start. Rather than needing a
 * restart, fetch it again -- rate-limited, so a genuinely broken chain does not turn
 * into a fetch loop.
 */
async function refreshCaIfStale(error) {
  if (!STALE_CA_PATTERN.test(error)) return
  if (Date.now() - lastCaRefresh < CA_REFRESH_INTERVAL_MS) return
  lastCaRefresh = Date.now()

  try {
    const fetched = await waitForCa({
      host: settings.proxyHost,
      port: settings.proxyPort,
      giveUpAfterMs: 5000,
    })
    if (fetched.fingerprint === ca?.fingerprint) return
    log('proxy-ca-changed', { from: ca?.fingerprint, to: fetched.fingerprint })
    ca = fetched
  } catch (cause) {
    log('proxy-ca-refresh-failed', { error: cause.message })
  }
}

/**
 * Workers are numbered. A worker exits once its number falls outside the current
 * concurrency, which is how the UI can turn the load down without restarting anything.
 */
const active = new Set()

function ensureWorkers() {
  while (!stopping && active.size < settings.concurrency) {
    let index = 0
    while (active.has(index)) index += 1
    active.add(index)
    runWorker(index).finally(() => active.delete(index))
  }
}

async function runWorker(index) {
  while (!stopping && index < settings.concurrency) {
    if (settings.paused) {
      await delay(200)
      continue
    }
    if (settings.burst > 0 && completed >= settings.burst) return

    try {
      await execute(nextRequest(rng, settings))
    } catch (cause) {
      log('worker-error', { index, error: cause.message })
    }

    // Each worker takes its share of the overall rate, re-read every iteration so a
    // change from the UI applies immediately.
    const perWorkerDelay = (60_000 / Math.max(1, settings.ratePerMinute)) * settings.concurrency
    await delay(Math.max(0, perWorkerDelay))
  }
}

function summarise() {
  const stats = store.stats()
  log('summary', {
    total: stats.total,
    ok: stats.ok,
    failed: stats.failed,
    successRate: stats.successRate === null ? null : Number(stats.successRate.toFixed(4)),
    http: stats.byScheme.http,
    https: stats.byScheme.https,
    intercepted: stats.intercepted,
    pii: stats.pii,
    latencyMs: stats.latencyMs,
  })
  return stats
}

async function main() {
  log('starting', { config: publicSettings(), burst: settings.burst || null })

  // Also the readiness gate: the proxy answering /ca.crt means it is up, and the CA is
  // needed before a single HTTPS request can succeed.
  ca = await waitForCa({
    host: settings.proxyHost,
    port: settings.proxyPort,
    // Bounded, so a proxy that never starts fails the run instead of hanging it. Compose
    // restarts the sender, which is the right shape for a long-running harness and gives
    // CI a non-zero exit rather than a timeout.
    giveUpAfterMs: settings.startupTimeoutMs,
    onAttempt: (attempt, reason) => {
      if (attempt === 1 || attempt % 10 === 0) log('waiting-for-proxy', { attempt, reason })
    },
  })
  log('trusting-proxy-ca', { subject: ca.subject, fingerprint: ca.fingerprint, validTo: ca.validTo })

  startUi({
    store,
    hooks: {
      ca: () => (ca ? { ...ca, pem: undefined, trusted: true } : { trusted: false }),
      fire: (scenario, scheme) => execute(build(scenario, rng, scheme)),
    },
  })

  if (settings.burst > 0) {
    ensureWorkers()
    while (completed < settings.burst) await delay(100)
    stopping = true
    const stats = summarise()
    const rate = stats.successRate ?? 0
    const passed = rate >= settings.minSuccessRate
    log(passed ? 'burst-passed' : 'burst-failed', {
      successRate: Number(rate.toFixed(4)),
      required: settings.minSuccessRate,
    })
    process.exit(passed ? 0 : 1)
  }

  ensureWorkers()
  setInterval(ensureWorkers, 500).unref()
  setInterval(summarise, 30_000).unref()
}

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    stopping = true
    summarise()
    process.exit(0)
  })
}

main().catch((cause) => {
  log('fatal', { error: cause.message, stack: cause.stack })
  process.exit(1)
})
