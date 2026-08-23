/**
 * One pass of the corpus through the proxy.
 *
 * Each document goes out as the user message of a chat completion, because that is the
 * shape the product is actually for: a person pastes a ticket, a contact list, a mail
 * thread into an assistant, and the question is what leaves the building. The receiver
 * plays the assistant and quotes the prompt back, which is what makes the return trip
 * observable -- a stand-in that is never sent back is never seen to be restored.
 *
 * Nothing here decides whether the proxy did well. It collects three copies of every
 * document and hands them to analyse(); the judgement lives there, next to the policy.
 */
import { setTimeout as delay } from 'node:timers/promises'

import { analyse } from './analyse.js'
import { send } from './proxyClient.js'
import { runIdFor, beginRun, writeDocument, writeSummary, writeReport, writeGroundTruth } from './runs.js'
import { renderReport } from './report.js'

const log = (event, fields = {}) =>
  console.log(JSON.stringify({ at: new Date().toISOString(), event, ...fields }))

const SYSTEM_PROMPT = 'Du bist ein Assistent für die interne Kundenbetreuung. Fasse zusammen und beantworte Rückfragen.'

/** The body one document goes out in. */
function bodyFor(document) {
  return JSON.stringify(
    {
      model: 'harness-echo-1',
      messages: [
        { role: 'system', content: SYSTEM_PROMPT },
        { role: 'user', content: document.text },
      ],
    },
    null,
    2,
  )
}

/** The user message out of a body in that shape, whichever end it came from. */
function promptFrom(raw) {
  const parsed = JSON.parse(raw)
  const message = parsed?.messages?.at(-1)?.content
  if (typeof message !== 'string') throw new Error('no user message in the captured body')
  return message
}

export function createRunner({ documents, groundTruth, settings, receiver, ca, gazetteer, policy }) {
  /** Null when idle. The UI polls this, so it carries everything a progress bar needs. */
  let progress = null
  let queued = 0

  const state = () => (progress ? { ...progress, queued } : { active: false, queued })

  /** Sends one document and works out what became of it. */
  async function evaluate(runId, document) {
    const entities = groundTruth.byDocument.get(document.id) ?? []
    const sent = document.text
    const startedAt = Date.now()

    const outcome = await send(
      {
        method: 'POST',
        path: '/api/v1/chat/completions',
        scheme: settings.scheme,
        proxyTls: settings.proxyTls && settings.proxyTlsPort > 0,
        headers: {
          'content-type': 'application/json; charset=utf-8',
          // How the receiver files what it saw, and how this process asks for it back.
          // Both ends are the harness; nothing here is a trust boundary.
          'x-eval-run': runId,
          'x-eval-doc': document.id,
        },
        body: bodyFor(document),
        expect: 200,
      },
      {
        proxyHost: settings.proxyHost,
        proxyPort: settings.proxyPort,
        proxyTlsPort: settings.proxyTlsPort,
        targetHost: settings.targetHost,
        targetHttpPort: settings.targetHttpPort,
        targetHttpsPort: settings.targetHttpsPort,
        timeoutMs: settings.requestTimeoutMs,
        caPem: ca?.pem,
      },
    )

    const transport = {
      scheme: settings.scheme,
      proxyTls: Boolean(settings.proxyTls && settings.proxyTlsPort > 0),
      status: outcome.status,
      durationMs: Number(outcome.durationMs.toFixed(1)),
      requestBytes: Buffer.byteLength(bodyFor(document)),
      responseBytes: outcome.responseBytes,
      intercepted: Boolean(outcome.tls?.issuer),
      error: outcome.error,
    }

    // A failed exchange is a result, not an exception. The run carries on and the
    // document is reported as failed, because half a corpus scored is worth having and
    // an aborted run is not.
    if (!outcome.ok) {
      return {
        document: describe(document, entities),
        transport,
        failure: outcome.error ?? `the destination answered ${outcome.status}`,
        sent,
        received: null,
        returned: null,
        analysis: null,
      }
    }

    let returned = null
    let failure = null
    try {
      const answer = JSON.parse(outcome.responseBody)
      // The receiver puts the prompt back verbatim under `echo` for an eval request. The
      // `choices` array beside it is the ordinary answer and is left alone.
      if (typeof answer?.echo !== 'string') throw new Error('the receiver did not echo the prompt back')
      returned = answer.echo
    } catch (cause) {
      failure = `the response could not be read: ${cause.message}`
    }

    let received = null
    try {
      const captured = await receiver.capture(runId, document.id)
      if (!captured) throw new Error('the receiver captured nothing under this id')
      received = promptFrom(captured.body)
    } catch (cause) {
      failure ??= `what the destination host saw could not be read: ${cause.message}`
    }

    return {
      document: describe(document, entities),
      transport: { ...transport, totalMs: Date.now() - startedAt },
      failure,
      sent,
      received,
      returned,
      analysis: received === null && returned === null ? null : analyse({ sent, received, returned, entities }),
    }
  }

  /** Runs the whole corpus once and writes it to disk. Resolves with the summary. */
  async function runOnce(dataDir, label) {
    const runId = runIdFor()
    await beginRun(dataDir, runId)
    await writeGroundTruth(dataDir, runId, {
      derivedAt: new Date().toISOString(),
      gazetteer,
      policy: { redact: [...policy.redact].sort(), informational: [...policy.informational].sort(), keep: policy.keep },
      totals: groundTruth.totals,
      byDocument: Object.fromEntries(groundTruth.byDocument),
    })

    progress = {
      active: true,
      runId,
      label: label ?? null,
      startedAt: new Date().toISOString(),
      total: documents.length,
      done: 0,
      current: null,
      leaked: 0,
      failed: 0,
    }

    const results = []
    const pending = [...documents]

    async function worker() {
      while (pending.length > 0) {
        const document = pending.shift()
        progress.current = document.id
        const result = await evaluate(runId, document)

        await writeDocument(dataDir, runId, document.id, {
          sent: result.sent,
          received: result.received,
          returned: result.returned,
          // The bodies live beside this file, so the JSON carries only the judgement.
          result: { document: result.document, transport: result.transport, failure: result.failure, analysis: result.analysis },
        })

        results.push(result)
        progress.done += 1
        progress.leaked += result.analysis?.counts.leaked ?? 0
        if (result.failure) progress.failed += 1
        if (settings.delayMs > 0) await delay(settings.delayMs)
      }
    }

    try {
      await Promise.all(Array.from({ length: Math.min(settings.concurrency, documents.length) }, worker))
    } finally {
      progress.current = null
    }

    // In document order rather than completion order, so two runs of the same corpus
    // produce reports that can be diffed line for line.
    const order = new Map(documents.map((document, index) => [document.id, index]))
    results.sort((a, b) => order.get(a.document.id) - order.get(b.document.id))

    const summary = summarise({ runId, label, results, settings, ca, gazetteer, policy, groundTruth, startedAt: progress.startedAt })
    await writeSummary(dataDir, runId, summary)
    await writeReport(dataDir, runId, renderReport(summary))

    // The captures are megabytes of request bodies held in the receiver's memory and are
    // on disk here now. Failing to free them is how a long afternoon of runs ends.
    await receiver.release(runId).catch(() => {})

    progress = null
    return summary
  }

  /** Queues n passes back to back. Returns immediately; the UI watches state(). */
  function start(dataDir, times, label, onFinished) {
    queued += Math.max(1, Math.trunc(times) || 1)
    if (progress) return { queued }

    const requested = queued
    void (async () => {
      while (queued > 0) {
        queued -= 1
        try {
          const summary = await runOnce(dataDir, label)
          log('run-finished', { runId: summary.runId, ...summary.headline })
          onFinished?.(summary)
        } catch (cause) {
          // Nothing is watching this promise, so an error that is not logged here is an
          // error nobody ever sees: the UI simply shows a run that never started.
          progress = null
          log('run-failed', { error: cause.message, stack: cause.stack })
          onFinished?.({ error: cause.message })
        }
      }
    })()

    return { queued: requested }
  }

  function cancel() {
    // Only the queue is cancellable. A pass in flight finishes: a half-written run
    // directory is worse than one more minute of waiting.
    const dropped = queued
    queued = 0
    return { dropped }
  }

  return { state, start, cancel, runOnce }
}

function describe(document, entities) {
  return {
    id: document.id,
    title: document.title,
    category: document.category,
    path: document.path,
    bytes: document.bytes,
    entities: entities.length,
    redactable: entities.filter((entity) => entity.policy === 'redact').length,
  }
}

/** Rolls the per-document results into the object the UI lists and the report renders. */
function summarise({ runId, label, results, settings, ca, gazetteer, policy, groundTruth, startedAt }) {
  const totals = {
    documents: results.length,
    failed: 0,
    entities: 0,
    redactable: 0,
    replaced: 0,
    partial: 0,
    leaked: 0,
    overRedacted: 0,
    damaged: 0,
    substitutions: 0,
    expected: 0,
    'over-redaction': 0,
    collateral: 0,
    unclassified: 0,
    strong: 0,
    strongHidden: 0,
    weak: 0,
    weakHidden: 0,
    clean: 0,
    intact: 0,
    faithful: 0,
  }
  const byKind = {}
  const byCategory = {}
  /**
   * Substitutions worth a human look, gathered across the whole run rather than one
   * document at a time -- with forty-nine documents nobody finds these by clicking.
   *
   * Two kinds, and they are not the same accusation. `over-redaction` is measured against
   * the policy in force: the value was on the keep list and the proxy changed it anyway.
   * `unclassified` is not an accusation at all -- the proxy changed something the ground
   * truth has no entry for, which is as often the proxy being right about something the
   * derivation missed as it is the proxy reaching too far. Both are listed, neither is
   * scored, and reading a few is the only way to tell them apart.
   */
  const toReview = []
  const seenOverRedaction = new Set()

  for (const result of results) {
    if (result.failure) totals.failed += 1
    const analysis = result.analysis
    if (!analysis) continue

    for (const replacement of analysis.replacements) {
      if (replacement.verdict !== 'over-redaction' && replacement.verdict !== 'unclassified') continue

      // A word diff splits `Güterstrasse 24, 3008 Bern` into two substitutions, and two
      // rows for one value reads as two faults. Report the value that was protected, once.
      if (replacement.verdict === 'over-redaction') {
        const entity = replacement.touched.find((candidate) => candidate.policy === 'keep')
        const key = `${result.document.id} ${entity?.index}`
        if (seenOverRedaction.has(key)) continue
        seenOverRedaction.add(key)
        toReview.push({
          document: result.document.id,
          verdict: replacement.verdict,
          before: entity?.text ?? replacement.before,
          after: replacement.after,
        })
        continue
      }

      toReview.push({
        document: result.document.id,
        verdict: replacement.verdict,
        before: replacement.before,
        after: replacement.after,
      })
    }

    for (const key of Object.keys(analysis.counts)) totals[key] += analysis.counts[key]
    if (analysis.clean) totals.clean += 1
    if (analysis.intact) totals.intact += 1
    if (analysis.faithful) totals.faithful += 1

    for (const [kind, counts] of Object.entries(analysis.byKind)) {
      const bucket = (byKind[kind] ??= { total: 0, replaced: 0, partial: 0, leaked: 0, damaged: 0 })
      for (const key of Object.keys(counts)) bucket[key] += counts[key]
    }

    const category = (byCategory[result.document.category] ??= { documents: 0, redactable: 0, leaked: 0, damaged: 0 })
    category.documents += 1
    category.redactable += analysis.counts.redactable
    category.leaked += analysis.counts.leaked
    category.damaged += analysis.counts.damaged
  }

  const ratio = (part, whole) => (whole === 0 ? null : part / whole)
  const hidden = totals.replaced + totals.partial

  const headline = {
    documents: totals.documents,
    failed: totals.failed,
    redactable: totals.redactable,
    leaked: totals.leaked,
    /** Of everything that had to be hidden, how much was. The number that matters. */
    coverage: ratio(hidden, totals.redactable),
    /** The same for unambiguous references only, which is the fair comparison. */
    strongCoverage: ratio(totals.strongHidden, totals.strong),
    weakCoverage: ratio(totals.weakHidden, totals.weak),
    /**
     * Of the substitutions the policy actually has a view on, how many were right.
     *
     * The denominator is the hits plus the keep-list violations, and nothing else. A
     * substitution landing on a company name or a town is `collateral` and one landing
     * outside the ground truth is `unclassified`; both are cases this harness has said in
     * as many words that it does not judge, so counting either as a miss here would judge
     * them anyway -- and would mean the more the ground truth declines to assert, the
     * worse the proxy scores.
     */
    precision: ratio(totals.expected, totals.expected + totals['over-redaction']),
    /**
     * The floor: every substitution the ground truth cannot vouch for counted as wrong.
     * The true figure is between this and the one above, and the gap is exactly the list
     * under "worth a look".
     */
    strictPrecision: ratio(totals.expected, totals.substitutions),
    /** Values that had to survive and did not. */
    overRedacted: totals.overRedacted,
    /** Values the client sent and did not get back. */
    damaged: totals.damaged,
    restoration: ratio(totals.redactable - totals.damaged, totals.redactable),
    cleanDocuments: totals.clean,
    intactDocuments: totals.intact,
  }

  return {
    runId,
    label: label ?? null,
    startedAt,
    finishedAt: new Date().toISOString(),
    config: {
      scheme: settings.scheme,
      proxyTls: Boolean(settings.proxyTls && settings.proxyTlsPort > 0),
      concurrency: settings.concurrency,
      proxy: `${settings.proxyHost}:${settings.proxyPort}`,
      target: settings.targetHost,
      caSubject: ca?.subject ?? null,
      caFingerprint: ca?.fingerprint ?? null,
    },
    groundTruth: {
      people: gazetteer.people.length,
      organisations: gazetteer.organisations.length,
      totals: groundTruth.totals,
      policy: { redact: [...policy.redact].sort(), informational: [...policy.informational].sort(), keep: policy.keep },
    },
    headline,
    totals,
    byKind,
    byCategory,
    // Ordered so the policy violations come first: those are measurable against a rule
    // someone wrote down. The unclassified ones below them are a lead, not a verdict.
    toReview: toReview.sort((a, b) =>
      a.verdict === b.verdict ? a.document.localeCompare(b.document, 'en') : a.verdict === 'over-redaction' ? -1 : 1,
    ),
    documents: results.map((result) => ({
      ...result.document,
      transport: result.transport,
      failure: result.failure,
      counts: result.analysis?.counts ?? null,
      clean: result.analysis?.clean ?? null,
      intact: result.analysis?.intact ?? null,
      faithful: result.analysis?.faithful ?? null,
    })),
  }
}
