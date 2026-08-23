/**
 * The evaluator: runs a corpus of documents through the proxy and scores what happened
 * to the personal data in them.
 *
 * The sender next door answers "does the proxy stay up and stay honest under traffic".
 * This process answers a different question: over a fixed set of realistic documents,
 * how much of what should have been hidden was, how much of what should have survived
 * did, and did the client get its own data back. Same proxy, same receiver, same shapes
 * on the wire -- a corpus instead of a random walk, and a verdict instead of a counter.
 */
import { readFile, readdir } from 'node:fs/promises'

import { settings, publicSettings } from './config.js'
import { loadCorpus } from './corpus.js'
import { buildGazetteer } from './gazetteer.js'
import { buildGroundTruth, resolvePolicy, loadOverrides } from './groundtruth.js'
import { createReceiverClient } from './receiverClient.js'
import { createRunner } from './runner.js'
import { waitForCa } from './ca.js'
import { ensureDataDir } from './runs.js'
import { startUi } from './ui.js'

const log = (event, fields = {}) =>
  console.log(JSON.stringify({ at: new Date().toISOString(), event, ...fields }))

/** An optional policy.json, so the corpus's rules can be changed without a rebuild. */
async function loadPolicy(file) {
  if (!file) return resolvePolicy()
  try {
    const overrides = JSON.parse(await readFile(file, 'utf8'))
    log('policy-loaded', { file })
    return resolvePolicy(overrides)
  } catch (cause) {
    log('policy-default', { file, reason: cause.message })
    return resolvePolicy()
  }
}

async function main() {
  log('starting', { config: publicSettings(), corpusDir: settings.corpusDir, dataDir: settings.dataDir })

  await ensureDataDir(settings.dataDir)

  const documents = await loadCorpus(settings.corpusDir)
  if (documents.length === 0) throw new Error(`no .md files under ${settings.corpusDir}`)

  const policy = await loadPolicy(settings.policyFile)
  const gazetteer = buildGazetteer(documents)

  // Hand-corrected sidecars win over the derivation where they exist. A correction that
  // has to be re-made every run is not a correction.
  const { overrides, problems } = await loadOverrides(settings.groundTruthDir, documents, { readFile, readdir })
  for (const problem of problems) log('ground-truth-problem', { detail: problem })

  const groundTruth = buildGroundTruth(documents, gazetteer, policy, overrides)

  log('corpus-loaded', {
    documents: documents.length,
    people: gazetteer.people.length,
    organisations: gazetteer.organisations.length,
    entities: groundTruth.totals.redact + groundTruth.totals.keep + groundTruth.totals.informational,
    mustBeHidden: groundTruth.totals.redact,
    mustSurvive: groundTruth.totals.keep,
    handCorrectedDocuments: groundTruth.totals.corrected,
  })

  // Also the readiness gate: the proxy answering /ca.crt means it is up, and the CA is
  // needed before a single https exchange can succeed.
  const ca = await waitForCa({
    host: settings.proxyHost,
    port: settings.proxyPort,
    giveUpAfterMs: settings.startupTimeoutMs,
    onAttempt: (attempt, reason) => {
      if (attempt === 1 || attempt % 10 === 0) log('waiting-for-proxy', { attempt, reason })
    },
  })
  log('trusting-proxy-ca', { subject: ca.subject, fingerprint: ca.fingerprint, validTo: ca.validTo })

  const receiver = createReceiverClient({ baseUrl: settings.receiverBaseUrl })
  try {
    log('receiver-ready', await receiver.ready())
  } catch (cause) {
    // Not fatal on its own: the run will report every document as unreadable at the
    // destination, which is a clearer message than refusing to start.
    log('receiver-unreachable', { url: settings.receiverBaseUrl, error: cause.message })
  }

  const runner = createRunner({ documents, groundTruth, settings, receiver, ca, gazetteer, policy })

  startUi({ documents, groundTruth, gazetteer, policy, runner, ca })

  // One pass at start when asked for, so the harness can be a CI step rather than a page
  // someone has to click. Exits non-zero when anything leaked or anything failed.
  if (process.env.EVAL_ON_START === 'true') {
    const summary = await runner.runOnce(settings.dataDir, 'automatic run at start')
    log('run-finished', { runId: summary.runId, ...summary.headline })
    if (process.env.EVAL_EXIT_AFTER_RUN === 'true') {
      const passed = summary.headline.leaked === 0 && summary.headline.failed === 0 && summary.headline.damaged === 0
      log(passed ? 'evaluation-passed' : 'evaluation-failed', summary.headline)
      process.exit(passed ? 0 : 1)
    }
  }
}

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    log('stopping', { signal })
    process.exit(0)
  })
}

main().catch((cause) => {
  log('fatal', { error: cause.message, stack: cause.stack })
  process.exit(1)
})
