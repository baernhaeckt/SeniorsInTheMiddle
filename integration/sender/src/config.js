/**
 * Everything the sender can be told, from the environment at start and from the testing
 * UI while it runs. One mutable object, because the UI edits the same knobs the loop
 * reads on its next iteration.
 */

function num(name, fallback) {
  const raw = process.env[name]
  if (raw === undefined || raw.trim() === '') return fallback
  const value = Number(raw)
  return Number.isFinite(value) ? value : fallback
}

export const settings = {
  // Where the traffic is pointed. The sender never talks to the receiver directly for
  // its traffic -- only through the proxy. That is the whole contract it tests.
  proxyHost: process.env.PROXY_HOST ?? 'proxy',
  proxyPort: num('PROXY_PORT', 3128),
  // The same proxy behind TLS: the listener a browser configured with an "HTTPS proxy"
  // would use. 0 or less means the proxy under test has no such listener, and the
  // sender sends everything plain regardless of PROXY_TLS_RATIO.
  proxyTlsPort: num('PROXY_TLS_PORT', 3127),
  targetHost: process.env.TARGET_HOST ?? 'receiver.sitm.local',
  targetHttpPort: num('TARGET_HTTP_PORT', 3000),
  targetHttpsPort: num('TARGET_HTTPS_PORT', 3443),

  // The one direct call: the receiver's own counters, so the UI can show both ends.
  receiverStatsUrl: process.env.RECEIVER_STATS_URL ?? 'http://receiver:3000/_harness/stats',

  uiPort: num('UI_PORT', 3100),

  // Live knobs.
  ratePerMinute: num('RATE_PER_MINUTE', 120),
  concurrency: clamp(num('CONCURRENCY', 4), 1, 64),
  httpsRatio: clamp(num('HTTPS_RATIO', 0.5), 0, 1),
  /** Share of requests whose connection to the proxy itself is TLS (the :3127 listener). */
  proxyTlsRatio: clamp(num('PROXY_TLS_RATIO', 0.5), 0, 1),
  piiRatio: clamp(num('PII_RATIO', 0.35), 0, 1),
  paused: false,

  // Fixed for the process.
  seed: num('SEED', 20260822),
  burst: num('BURST', 0),
  minSuccessRate: clamp(num('MIN_SUCCESS_RATE', 0.95), 0, 1),
  requestTimeoutMs: num('REQUEST_TIMEOUT_MS', 30000),
  /** How long to wait for the proxy to answer /ca.crt before giving up on the run. */
  startupTimeoutMs: num('STARTUP_TIMEOUT_MS', 120000),
  historySize: num('HISTORY_SIZE', 200),
  previewBytes: num('PREVIEW_BYTES', 2000),
}

export function clamp(value, min, max) {
  if (!Number.isFinite(value)) return min
  return Math.min(max, Math.max(min, value))
}

/** Applies what the UI sent, ignoring anything it is not allowed to change. */
export function updateSettings(patch) {
  if ('ratePerMinute' in patch) settings.ratePerMinute = clamp(Number(patch.ratePerMinute), 1, 20000)
  if ('concurrency' in patch) settings.concurrency = Math.round(clamp(Number(patch.concurrency), 1, 64))
  if ('httpsRatio' in patch) settings.httpsRatio = clamp(Number(patch.httpsRatio), 0, 1)
  if ('proxyTlsRatio' in patch) settings.proxyTlsRatio = clamp(Number(patch.proxyTlsRatio), 0, 1)
  if ('piiRatio' in patch) settings.piiRatio = clamp(Number(patch.piiRatio), 0, 1)
  if ('paused' in patch) settings.paused = Boolean(patch.paused)
  return publicSettings()
}

export function publicSettings() {
  return {
    proxy: `${settings.proxyHost}:${settings.proxyPort}`,
    proxyTls: settings.proxyTlsPort > 0 ? `${settings.proxyHost}:${settings.proxyTlsPort}` : null,
    target: settings.targetHost,
    targetHttpPort: settings.targetHttpPort,
    targetHttpsPort: settings.targetHttpsPort,
    ratePerMinute: settings.ratePerMinute,
    concurrency: settings.concurrency,
    httpsRatio: settings.httpsRatio,
    proxyTlsRatio: settings.proxyTlsRatio,
    piiRatio: settings.piiRatio,
    paused: settings.paused,
    seed: settings.seed,
  }
}
