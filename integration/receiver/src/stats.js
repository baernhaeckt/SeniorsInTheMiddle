/** Counters the receiver exposes at /_harness/stats. Plain object, no dependencies. */
export function createStats() {
  return {
    startedAt: Date.now(),
    total: 0,
    byScheme: { http: 0, https: 0 },
    byRoute: {},
    byStatus: {},
    bytesIn: 0,
    bytesOut: 0,
    /** Bodies the receiver actually read, i.e. everything that was not a static asset. */
    bodiesInspected: 0,
    /** The number that matters: bodies that still carried real identifiers. */
    sawRawPii: 0,
    /** Bodies that arrived with `[PERSON_1]`-style stand-ins instead. */
    sawTokens: 0,
    piiKinds: {},
  }
}

export function record(stats, entry) {
  stats.total += 1
  stats.byScheme[entry.scheme] = (stats.byScheme[entry.scheme] ?? 0) + 1
  stats.byRoute[entry.route] = (stats.byRoute[entry.route] ?? 0) + 1
  stats.byStatus[entry.status] = (stats.byStatus[entry.status] ?? 0) + 1
  stats.bytesIn += entry.requestBytes
  stats.bytesOut += entry.responseBytes

  if (!entry.inspection) return

  stats.bodiesInspected += 1
  if (entry.inspection.sawRawPii) stats.sawRawPii += 1
  if (entry.inspection.tokenCount > 0) stats.sawTokens += 1
  for (const [kind, count] of Object.entries(entry.inspection.kinds)) {
    stats.piiKinds[kind] = (stats.piiKinds[kind] ?? 0) + count
  }
}

export function snapshot(stats) {
  return { ...stats, uptimeMs: Date.now() - stats.startedAt }
}
