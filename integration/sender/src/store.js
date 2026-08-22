/**
 * What the sender remembers: a rolling window of exchanges for the table in the UI, and
 * running totals for everything above it. Bounded on purpose -- this process is meant to
 * run for hours without anyone watching it.
 */

export function createStore({ historySize = 200, latencySamples = 500 } = {}) {
  const history = []
  const latencies = []
  let seq = 0

  const totals = {
    startedAt: Date.now(),
    total: 0,
    ok: 0,
    failed: 0,
    transportErrors: 0,
    bytesSent: 0,
    bytesReceived: 0,
    byScheme: { http: { total: 0, ok: 0 }, https: { total: 0, ok: 0 } },
    // How the proxy itself was reached: plain TCP on the proxy port, or TLS on the
    // TLS proxy port. A regression in the TLS listener shows up only under `tls`.
    byTransport: { plain: { total: 0, ok: 0 }, tls: { total: 0, ok: 0 } },
    byScenario: {},
    byStatus: {},
    errors: {},
    intercepted: 0,
    pii: { sent: 0, intact: 0, broken: 0, tokensInResponse: 0 },
  }

  function add(record) {
    record.seq = ++seq
    history.push(record)
    if (history.length > historySize) history.shift()

    latencies.push(record.durationMs)
    if (latencies.length > latencySamples) latencies.shift()

    totals.total += 1
    totals[record.ok ? 'ok' : 'failed'] += 1
    if (record.error) {
      totals.transportErrors += 1
      totals.errors[record.error] = (totals.errors[record.error] ?? 0) + 1
    }
    totals.bytesSent += record.requestBytes
    totals.bytesReceived += record.responseBytes

    const scheme = totals.byScheme[record.scheme]
    scheme.total += 1
    if (record.ok) scheme.ok += 1

    const transport = totals.byTransport[record.proxyTls ? 'tls' : 'plain']
    transport.total += 1
    if (record.ok) transport.ok += 1

    const scenario = (totals.byScenario[record.scenario] ??= { total: 0, ok: 0 })
    scenario.total += 1
    if (record.ok) scenario.ok += 1

    const status = record.status ?? 'none'
    totals.byStatus[status] = (totals.byStatus[status] ?? 0) + 1

    if (record.tls?.issuer) totals.intercepted += 1

    if (record.piiSent) {
      totals.pii.sent += 1
      totals.pii[record.piiIntact ? 'intact' : 'broken'] += 1
      if (record.tokensInResponse) totals.pii.tokensInResponse += 1
    }

    return record
  }

  function percentile(fraction) {
    if (latencies.length === 0) return null
    const sorted = [...latencies].sort((a, b) => a - b)
    const index = Math.min(sorted.length - 1, Math.floor(fraction * sorted.length))
    return Number(sorted[index].toFixed(1))
  }

  function stats() {
    return {
      ...totals,
      uptimeMs: Date.now() - totals.startedAt,
      successRate: totals.total === 0 ? null : totals.ok / totals.total,
      latencyMs: { p50: percentile(0.5), p95: percentile(0.95), max: percentile(1) },
    }
  }

  /** Everything newer than `since`, oldest first. */
  function events(since = 0) {
    const records = history.filter((record) => record.seq > since)
    return { records, latestSeq: seq }
  }

  return { add, stats, events, get seq() { return seq } }
}
