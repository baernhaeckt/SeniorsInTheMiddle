/**
 * Everything the evaluator can be told, from the environment at start and from the web
 * UI while it runs. Same shape as the sender's config for the same reason: one mutable
 * object, because the page edits the knobs the next run reads.
 */

function num(name, fallback) {
  const raw = process.env[name]
  if (raw === undefined || raw.trim() === '') return fallback
  const value = Number(raw)
  return Number.isFinite(value) ? value : fallback
}

function clamp(value, min, max) {
  if (!Number.isFinite(value)) return min
  return Math.min(max, Math.max(min, value))
}

export const settings = {
  // Where the traffic is pointed. As with the sender, nothing reaches the destination
  // except through the proxy -- that is the contract under test.
  proxyHost: process.env.PROXY_HOST ?? 'proxy',
  proxyPort: num('PROXY_PORT', 3128),
  proxyTlsPort: num('PROXY_TLS_PORT', 3127),
  targetHost: process.env.TARGET_HOST ?? 'receiver.sitm.local',
  targetHttpPort: num('TARGET_HTTP_PORT', 3000),
  targetHttpsPort: num('TARGET_HTTPS_PORT', 3443),

  /**
   * The one direct call. What the destination host received is read back from the
   * receiver over plain HTTP, deliberately not through the proxy: asking the proxy what
   * the proxy did would answer a different question.
   */
  receiverBaseUrl: process.env.RECEIVER_BASE_URL ?? 'http://receiver:3000',

  corpusDir: process.env.CORPUS_DIR ?? '/corpus',
  dataDir: process.env.DATA_DIR ?? '/data',
  policyFile: process.env.POLICY_FILE ?? '',
  /**
   * Hand-corrected ground truth, one `<DOC-ID>.json` per document. A file here is used in
   * preference to the derivation, which is what lets a correction stick rather than being
   * argued with on every run. Written by src/tools/dumpGroundTruth.js.
   */
  groundTruthDir: process.env.GROUNDTRUTH_DIR ?? '/groundtruth',

  uiPort: num('UI_PORT', 3200),

  // Live knobs, editable from the page.
  /** 'https' goes through CONNECT and interception; 'http' through absolute form. */
  scheme: process.env.EVAL_SCHEME === 'http' ? 'http' : 'https',
  /** Whether the connection to the proxy itself is TLS (the :3127 listener). */
  proxyTls: process.env.EVAL_PROXY_TLS === 'true',
  /** How many documents are in flight at once. One is the honest default: the proxy's */
  /** stand-in map is per client and per host, and hammering it proves nothing useful. */
  concurrency: clamp(num('EVAL_CONCURRENCY', 2), 1, 16),
  /** A pause between documents, for watching a run happen rather than seeing it finish. */
  delayMs: clamp(num('EVAL_DELAY_MS', 0), 0, 10_000),

  // Fixed for the process.
  requestTimeoutMs: num('REQUEST_TIMEOUT_MS', 120_000),
  startupTimeoutMs: num('STARTUP_TIMEOUT_MS', 180_000),
  /** Runs kept on disk. Older ones are listed until removed by hand; nothing is deleted. */
  maxRunsListed: num('MAX_RUNS_LISTED', 200),
}

export function updateSettings(patch) {
  if ('scheme' in patch) settings.scheme = patch.scheme === 'http' ? 'http' : 'https'
  if ('proxyTls' in patch) settings.proxyTls = Boolean(patch.proxyTls) && settings.proxyTlsPort > 0
  if ('concurrency' in patch) settings.concurrency = Math.round(clamp(Number(patch.concurrency), 1, 16))
  if ('delayMs' in patch) settings.delayMs = Math.round(clamp(Number(patch.delayMs), 0, 10_000))
  return publicSettings()
}

export function publicSettings() {
  return {
    proxy: `${settings.proxyHost}:${settings.proxyPort}`,
    proxyTlsAddress: settings.proxyTlsPort > 0 ? `${settings.proxyHost}:${settings.proxyTlsPort}` : null,
    target: settings.targetHost,
    scheme: settings.scheme,
    proxyTls: settings.proxyTls,
    concurrency: settings.concurrency,
    delayMs: settings.delayMs,
  }
}
