/**
 * The download: one run as a markdown file someone can read, mail on, or diff against
 * last week's.
 *
 * It leads with the two numbers that decide whether the thing works -- what leaked and
 * what did not come back -- and only then explains itself. A report whose headline is a
 * table of definitions gets skimmed to the bottom and closed.
 */

const percent = (value) => (value === null || value === undefined ? '—' : `${(value * 100).toFixed(1)}%`)
const count = (value) => (value ?? 0).toLocaleString('en-US')

/** Markdown table cells cannot carry a pipe or a newline. */
const cell = (value) => String(value ?? '').replace(/\|/g, '\\|').replace(/\r?\n/g, ' ')

function table(headers, rows, alignments = []) {
  if (rows.length === 0) return '_none_\n'
  const rule = headers.map((_, index) => (alignments[index] === 'right' ? '---:' : '---'))
  return [
    `| ${headers.join(' | ')} |`,
    `| ${rule.join(' | ')} |`,
    ...rows.map((row) => `| ${row.map(cell).join(' | ')} |`),
    '',
  ].join('\n')
}

function verdict(summary) {
  const { headline, totals } = summary
  if (totals.documents === 0) return 'No documents were evaluated.'
  if (headline.failed === totals.documents) return '**Every document failed to get through the proxy.** Nothing was measured.'

  const parts = []
  parts.push(
    headline.leaked === 0
      ? `**Nothing leaked.** All ${count(headline.redactable)} protected values were replaced before the destination host saw the body.`
      : `**${count(headline.leaked)} of ${count(headline.redactable)} protected values reached the destination host unchanged** (${percent(headline.coverage)} covered).`,
  )
  parts.push(
    headline.damaged === 0
      ? 'Every value the client sent came back to the client.'
      : `**${count(headline.damaged)} ${headline.damaged === 1 ? 'value' : 'values'} did not come back to the client**, so the round trip is lossy.`,
  )
  if (headline.overRedacted > 0) {
    parts.push(
      `${count(headline.overRedacted)} values on the keep list were changed anyway — a difference from the policy in force, not necessarily a fault.`,
    )
  }
  if (headline.failed > 0) parts.push(`${count(headline.failed)} documents never completed an exchange.`)
  return parts.join(' ')
}

export function renderReport(summary) {
  const { headline, totals, byKind, byCategory, config, groundTruth } = summary
  const out = []

  out.push(`# Replacement quality — run ${summary.runId}`)
  if (summary.label) out.push(`\n_${summary.label}_`)
  out.push('')
  out.push(verdict(summary))
  out.push('')

  out.push('## Headline')
  out.push('')
  out.push(
    table(
      ['Measure', 'Value', 'What it means'],
      [
        ['Leaked', count(headline.leaked), 'Protected values the destination host saw unchanged. Must be 0.'],
        ['Coverage', percent(headline.coverage), 'Share of protected values that were replaced.'],
        ['Coverage, unambiguous only', percent(headline.strongCoverage), 'Full names and shaped identifiers.'],
        ['Coverage, bare names', percent(headline.weakCoverage), 'A surname or first name on its own. Harder, reported apart.'],
        ['Restoration', percent(headline.restoration), 'Share of protected values the client got back.'],
        ['Not returned', count(headline.damaged), 'Values the client sent and did not get back. Must be 0.'],
        ['Over-redaction', count(headline.overRedacted), 'Values on the keep list that were changed anyway. A policy difference, not necessarily a fault.'],
        ['Precision', percent(headline.precision), 'Of the substitutions the policy has a view on, how many landed on a protected value.'],
        ['Precision, strict floor', percent(headline.strictPrecision), 'Every substitution the ground truth cannot vouch for counted as wrong. The true figure is between the two, and the gap is the list under “worth a look”.'],
        ['Documents fully clean', `${count(headline.cleanDocuments)} / ${count(totals.documents)}`, 'Nothing protected reached the destination.'],
        ['Documents fully intact', `${count(headline.intactDocuments)} / ${count(totals.documents)}`, 'Everything came back to the client.'],
        ['Exchanges failed', count(headline.failed), 'Never completed a request through the proxy.'],
      ],
      ['', 'right', ''],
    ),
  )

  out.push('## By kind of value')
  out.push('')
  out.push(
    table(
      ['Kind', 'Expected', 'Replaced', 'Partly', 'Leaked', 'Not returned', 'Coverage'],
      Object.entries(byKind)
        .sort((a, b) => b[1].total - a[1].total)
        .map(([kind, counts]) => [
          kind,
          count(counts.total),
          count(counts.replaced),
          count(counts.partial),
          count(counts.leaked),
          count(counts.damaged),
          percent(counts.total === 0 ? null : (counts.replaced + counts.partial) / counts.total),
        ]),
      ['', 'right', 'right', 'right', 'right', 'right', 'right'],
    ),
  )

  out.push('## By document category')
  out.push('')
  out.push(
    table(
      ['Category', 'Documents', 'Protected values', 'Leaked', 'Not returned'],
      Object.entries(byCategory)
        .sort((a, b) => a[0].localeCompare(b[0], 'en'))
        .map(([category, counts]) => [
          category,
          count(counts.documents),
          count(counts.redactable),
          count(counts.leaked),
          count(counts.damaged),
        ]),
      ['', 'right', 'right', 'right', 'right'],
    ),
  )

  const toReview = summary.toReview ?? []
  if (toReview.length > 0) {
    out.push('## Substitutions worth a look')
    out.push('')
    out.push(
      'Neither group below is scored, and the two are not the same accusation.',
    )
    out.push('')
    out.push(
      '**over-redaction** is measured against the policy in force: the value was on the keep list in `policy.json` and was ' +
        'changed anyway. That list is a choice, not a law — a proxy that redacts every mail address including the published ' +
        'invoicing one is defensible, and moving the value out of `keep` is the right answer if that is the intent.',
    )
    out.push('')
    out.push(
      '**unclassified** is not an accusation at all. The proxy changed something the ground truth has no entry for, which is as ' +
        'often the proxy being right about something the derivation missed — an abbreviated first name, a hostname — as it is ' +
        'the proxy reaching too far into ordinary prose. Reading a few is the only way to tell them apart, which is why they ' +
        'are listed rather than counted.',
    )
    out.push('')
    out.push(
      table(
        ['Document', 'Verdict', 'Original', 'Became'],
        toReview.map((item) => [item.document, item.verdict, item.before, item.after]),
      ),
    )
  }

  out.push('## Documents')
  out.push('')
  out.push(
    table(
      ['Document', 'Category', 'Protected', 'Leaked', 'Not returned', 'Over-redacted', 'Status', 'ms'],
      summary.documents.map((document) => [
        `${document.id} — ${document.title}`,
        document.category,
        count(document.counts?.redactable),
        count(document.counts?.leaked),
        count(document.counts?.damaged),
        count(document.counts?.overRedacted),
        document.failure ? `failed: ${document.failure}` : document.clean && document.intact ? 'clean' : 'see detail',
        count(document.transport?.durationMs),
      ]),
      ['', '', 'right', 'right', 'right', 'right', '', 'right'],
    ),
  )

  out.push('## What was measured, and how')
  out.push('')
  out.push(
    'Every document was sent through the proxy as the user message of a chat completion. Three copies were then compared: ' +
      'the markdown as it left the evaluator, the same body as it arrived at the destination host (read back from the receiver ' +
      'directly, not through the proxy), and what came back to the client after the proxy restored the real values.',
  )
  out.push('')
  out.push(
    'The proxy substitutes plausible stand-ins rather than `[PERSON_1]`-style tokens, so a redacted body looks exactly like an ' +
      'unredacted one and searching it for personal-data shapes proves nothing. What the figures above rest on instead is the ' +
      'alignment between the copies: which spans of the document the proxy changed. A protected value inside a changed span was ' +
      'hidden; one inside an unchanged span leaked.',
  )
  out.push('')
  out.push(
    '**Partly** in the tables above means an identifying fragment of the value survived. A fragment counts as identifying when ' +
      'it contains a word of four letters or more — `Bern`, `Beat`, `Freiburgstrasse`, the parts a reader recognises — or when ' +
      'it covers more than 40% of the original, which catches half an AHV number even though it has no letters in it.',
  )
  out.push('')
  out.push(
    'Everything else is a remnant, not a partial hit. `Sulgenrainweg 80, 3250 Lyss` comes back with the street replaced and ' +
      'the town replaced and `80, 3250` still standing — a house number and a postcode, no word in either. Both identifying ' +
      'parts are gone, so that is a value the proxy hid. What survived is recorded on every finding and shown in the detail ' +
      'view regardless, so the judgement can be checked rather than taken on trust.',
  )
  out.push('')
  out.push(
    `Ground truth was derived from the corpus itself — ${count(groundTruth.people)} people and ` +
      `${count(groundTruth.organisations)} organisations, each vouched for by structure in the documents (a contact-list row, an ` +
      'angle-bracketed address, a chat prefix) or by a mail local part matching the name. Nothing is hand-listed, so the corpus ' +
      'can change without a dictionary changing with it.',
  )
  out.push('')
  if (groundTruth.totals?.corrected) {
    out.push(
      `**${count(groundTruth.totals.corrected)} documents were scored against a hand-corrected sidecar** in ` +
        '`evaluation/groundtruth/` rather than against the derivation. A sidecar wins wherever it exists, so an improvement to ' +
        'the derivation will not show up for those documents until the sidecar is refreshed or deleted.',
    )
    out.push('')
  }
  out.push('Policy applied to the ground truth:')
  out.push('')
  out.push(`- **Must be hidden**: ${groundTruth.policy.redact.join(', ')}`)
  out.push(`- **Must survive unchanged**: ${groundTruth.policy.keep.map((value) => `\`${value}\``).join(', ')}`)
  out.push(`- **Counted, never scored**: ${groundTruth.policy.informational.join(', ')} — whether these should be hidden is a policy question, so what happened to them is reported and left for the reader.`)
  out.push('')

  out.push('## Run')
  out.push('')
  out.push(
    table(
      ['Field', 'Value'],
      [
        ['Run', summary.runId],
        ['Started', summary.startedAt],
        ['Finished', summary.finishedAt],
        ['Proxy', config.proxy],
        ['Destination', config.target],
        ['Scheme', config.scheme === 'https' ? 'https (CONNECT, intercepted)' : 'http (absolute form)'],
        ['Connection to proxy', config.proxyTls ? 'tls (:3127)' : 'plain (:3128)'],
        ['Workers', String(config.concurrency)],
        ['Proxy CA', config.caSubject ?? '—'],
        ['Proxy CA fingerprint', config.caFingerprint ?? '—'],
        ['Substitutions made', count(totals.substitutions)],
        ['… on a protected value', count(totals.expected)],
        ['… on a value meant to survive', count(totals['over-redaction'])],
        ['… on a company name, town, date, contract or amount', count(totals.collateral)],
        ['… on something the ground truth does not cover', count(totals.unclassified)],
      ],
    ),
  )

  out.push('')
  out.push(
    '_Only the first two lines are scored. The rest are cases this harness has declined to judge — the corpus’s own company ' +
      'names, towns and contract numbers, and substitutions the ground truth has no entry for at all. Both are listed under ' +
      '“substitutions worth a look”, because calling them either way would be a guess._',
  )
  out.push('')

  return out.join('\n')
}

/** A single document's detail, for the per-document download. */
export function renderDocumentReport(summary, detail) {
  const out = []
  const analysis = detail.analysis

  out.push(`# ${detail.document.id} — ${detail.document.title}`)
  out.push('')
  out.push(`Run \`${summary.runId}\` · \`${detail.document.path}\` · ${count(detail.document.bytes)} bytes`)
  out.push('')

  if (detail.failure) {
    out.push(`> **This exchange did not complete:** ${detail.failure}`)
    out.push('')
  }

  if (analysis) {
    out.push(
      table(
        ['Protected', 'Replaced', 'Partly', 'Leaked', 'Not returned', 'Over-redacted', 'Substitutions'],
        [
          [
            count(analysis.counts.redactable),
            count(analysis.counts.replaced),
            count(analysis.counts.partial),
            count(analysis.counts.leaked),
            count(analysis.counts.damaged),
            count(analysis.counts.overRedacted),
            count(analysis.counts.substitutions),
          ],
        ],
        ['right', 'right', 'right', 'right', 'right', 'right', 'right'],
      ),
    )

    const leaked = analysis.findings.filter((finding) => finding.policy === 'redact' && finding.outcome === 'leaked')
    out.push('## Reached the destination host unchanged')
    out.push('')
    out.push(
      table(
        ['Line', 'Kind', 'Tier', 'Value'],
        leaked.map((finding) => [finding.line, finding.kind, finding.tier, finding.text]),
        ['right', '', '', ''],
      ),
    )

    const partly = analysis.findings.filter((finding) => finding.policy === 'redact' && finding.outcome === 'partial')
    if (partly.length > 0) {
      out.push('## Only partly replaced')
      out.push('')
      out.push(
        table(
          ['Line', 'Kind', 'Value', 'What survived'],
          partly.map((finding) => [finding.line, finding.kind, finding.text, (finding.remnants ?? finding.survived).join(' · ')]),
          ['right', '', '', ''],
        ),
      )
    }

    const overRedacted = analysis.findings.filter((finding) => finding.policy === 'keep' && finding.outcome !== 'leaked')
    if (overRedacted.length > 0) {
      out.push('## Rewritten although it had to survive')
      out.push('')
      out.push(
        table(
          ['Line', 'Kind', 'Value', 'Became'],
          overRedacted.map((finding) => [finding.line, finding.kind, finding.text, finding.substitute ?? '']),
          ['right', '', '', ''],
        ),
      )
    }

    out.push('## Substitutions the proxy made')
    out.push('')
    out.push(
      table(
        ['Verdict', 'Original', 'Seen by the destination'],
        analysis.replacements.map((replacement) => [replacement.verdict, replacement.before, replacement.after]),
      ),
    )

    if (analysis.restorationFailures.length > 0) {
      out.push('## Did not come back to the client')
      out.push('')
      out.push(
        table(
          ['Sent', 'Came back'],
          analysis.restorationFailures.map((failure) => [failure.before, failure.after]),
        ),
      )
    }
  }

  for (const [heading, body] of [
    ['What left the evaluator', detail.sent],
    ['What the destination host saw', detail.received],
    ['What came back to the client', detail.returned],
  ]) {
    out.push(`## ${heading}`)
    out.push('')
    out.push('```')
    out.push(body || '(nothing)')
    out.push('```')
    out.push('')
  }

  return out.join('\n')
}
