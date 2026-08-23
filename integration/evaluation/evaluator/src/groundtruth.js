import { join } from 'node:path'

/**
 * What is in each document, and what the proxy is expected to do about it.
 *
 * Every entity carries a policy, because "quality of replacement" is two questions and
 * they pull in opposite directions:
 *
 *   redact         must not reach the destination. Missing one is a leak.
 *   keep           must reach the destination unchanged. The published hotline, the
 *                  invoicing address, the no-reply sender -- redacting these is not a
 *                  safety win, it is a body arriving wrong for no benefit.
 *   informational  counted and shown, never scored. Company names, contract numbers,
 *                  amounts, ordinary dates. Whether they should be hidden is a policy
 *                  question this harness has no business deciding, so it reports what
 *                  happened to them and leaves the judgement to whoever reads it.
 *
 * Entities also carry a tier. `strong` is a full name or an identifier with a shape --
 * an unambiguous reference, and the number the harness leads with. `weak` is a bare
 * surname or first name that some other document ties to a person: still personal data,
 * genuinely harder to find, and reported on its own so it neither inflates the headline
 * figure nor disappears from it.
 */

/**
 * Identifiers with a shape specific enough to find without a dictionary. Ordered by how
 * much of the text they claim, longest first, so `simon.iseli@bremgarte.ch` is one
 * address rather than a name inside a domain.
 */
const PATTERNS = [
  // Freiburgstrasse 91, 3280 Murten -- also "3033 Wohlen bei Bern" and "2502 Biel/Bienne".
  [
    'ADDRESS',
    /\b[A-ZÄÖÜ][a-zäöüéèà]+(?:strasse|gasse|weg|platz|rain|allee|ring|matte|halde|feld|steig|hof|str\.)\s+\d+[a-z]?,\s*\d{4}\s+[A-ZÄÖÜ][\wäöüéèà]+(?:\/[A-ZÄÖÜ][\wäöüéèà]+)?(?:\s+bei\s+[A-ZÄÖÜ][\wäöüéèà]+)?/g,
  ],
  ['IBAN', /\bCH\d{2}(?:\s?\d{4}){4}\s?\d\b/g],
  ['EMAIL', /\b[\w.+-]+@[\w-]+(?:\.[\w-]+)*\.[a-z]{2,}\b/gi],
  ['AHV', /\b756\.\d{4}\.\d{4}\.\d{2}\b/g],
  ['PHONE', /\+41\s?\d{2}\s?\d{3}\s?\d{2}\s?\d{2}\b/g],
  // The corpus wraps a contract number across a line break in a couple of tickets --
  // `NAT-2025-\n4688` -- and a pattern that cannot see across the newline leaves the
  // proxy's substitution on the prefix looking like a rewrite of nothing in particular.
  ['CONTRACT_ID', /\bNAT-\d{4}-\r?\n?\d{4}\b/g],
  ['TICKET_ID', /\bK-\d{5}\b/g],
  ['MONEY', /\bCHF\s[\d'’]+(?:\.\d{2})?/g],
  ['DATE', /\b(?:19|20)\d{2}-\d{2}-\d{2}\b/g],
  // A host or a subnet. Not personal data on its own, but a detector that hides one is
  // doing something defensible rather than something stray, and saying so keeps it out of
  // the pile of substitutions nobody can account for.
  ['IP', /\b\d{1,3}(?:\.\d{1,3}){3}(?:\/\d{1,2})?\b/g],
]

/** A date is a birth date when the words in front of it say so. Otherwise it is a date. */
const BIRTHDATE_CUE = /(?:geboren am|Geburtsdatum|geb\.)\s*$/i

const DEFAULT_POLICY = {
  redact: ['PERSON', 'EMAIL', 'PHONE', 'IBAN', 'AHV', 'ADDRESS', 'BIRTHDATE'],
  informational: ['ORG', 'LOCATION', 'IP', 'DATE', 'CONTRACT_ID', 'TICKET_ID', 'MONEY'],
  /**
   * Values published on purpose. Everything here is the provider's own front door, which
   * appears in nearly every document precisely because it is not personal.
   */
  keep: [
    'rechnung@natron.io',
    'no-reply@ticketsystem.example',
    '+41 31 528 00 00',
    'Güterstrasse 24, 3008 Bern',
  ],
}

/**
 * Reads hand-corrected sidecars, if any. An entity whose offsets no longer match the
 * document is dropped and reported: a corpus edited after the sidecar was written would
 * otherwise score against text that is not there any more, and silently.
 */
export async function loadOverrides(directory, documents, { readFile, readdir }) {
  const overrides = new Map()
  const problems = []

  let files
  try {
    files = await readdir(directory)
  } catch {
    return { overrides, problems }
  }

  const byId = new Map(documents.map((document) => [document.id, document]))
  for (const file of files) {
    if (!file.endsWith('.json') || file === 'gazetteer.json') continue
    const id = file.slice(0, -5)
    const document = byId.get(id)
    if (!document) {
      problems.push(`${file}: no document with that id`)
      continue
    }

    try {
      const parsed = JSON.parse(await readFile(join(directory, file), 'utf8'))
      const entities = []
      for (const entity of parsed.entities ?? []) {
        if (document.text.slice(entity.start, entity.end) !== entity.text) {
          problems.push(`${file}: "${entity.text}" is not at ${entity.start}..${entity.end} any more`)
          continue
        }
        entities.push({ ...entity, index: entities.length })
      }
      overrides.set(id, entities)
    } catch (cause) {
      problems.push(`${file}: ${cause.message}`)
    }
  }

  return { overrides, problems }
}

export function resolvePolicy(overrides = {}) {
  return {
    redact: new Set(overrides.redact ?? DEFAULT_POLICY.redact),
    informational: new Set(overrides.informational ?? DEFAULT_POLICY.informational),
    keep: overrides.keep ?? DEFAULT_POLICY.keep,
  }
}

export { DEFAULT_POLICY }

/** Escapes a literal for use inside a RegExp. */
const quote = (value) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

/**
 * Keeps the entity that claims the most text at any position and drops what it swallows.
 * `Simon Iseli` next to `simon.iseli@bremgarte-medizintechnik.ch` are two entities; the
 * name that the regex also finds inside the address is not a third.
 */
function resolveOverlaps(entities) {
  const ordered = [...entities].sort((a, b) => a.start - b.start || b.end - b.start - (a.end - a.start))
  const kept = []
  for (const entity of ordered) {
    if (kept.some((other) => entity.start < other.end && other.start < entity.end)) continue
    kept.push(entity)
  }
  return kept.sort((a, b) => a.start - b.start)
}

/** Finds every occurrence of a literal on word boundaries. */
function occurrences(text, literal, make) {
  const found = []
  // \b does not fire next to `ü`, which is not a word character to a JavaScript RegExp,
  // so the boundary is spelled out: no letter, digit or dot may touch either end.
  const pattern = new RegExp(`(?<![\\p{L}\\d.])${quote(literal)}(?![\\p{L}\\d])`, 'gu')
  for (let match; (match = pattern.exec(text)) !== null; ) {
    found.push(make(match.index, match.index + literal.length))
  }
  return found
}

/**
 * @param document   { id, text }
 * @param gazetteer  from buildGazetteer, shared across the corpus
 * @param policy     from resolvePolicy
 */
export function groundTruthFor(document, gazetteer, policy) {
  const text = document.text
  const candidates = []

  for (const [kind, pattern] of PATTERNS) {
    pattern.lastIndex = 0
    for (let match; (match = pattern.exec(text)) !== null; ) {
      const actual =
        kind === 'DATE' && BIRTHDATE_CUE.test(text.slice(Math.max(0, match.index - 24), match.index))
          ? 'BIRTHDATE'
          : kind
      candidates.push({
        kind: actual,
        text: match[0],
        start: match.index,
        end: match.index + match[0].length,
        tier: 'strong',
      })
    }
  }

  for (const person of gazetteer.people) {
    candidates.push(...occurrences(text, person, (start, end) => ({ kind: 'PERSON', text: person, start, end, tier: 'strong' })))
  }

  for (const organisation of gazetteer.organisations) {
    candidates.push(
      ...occurrences(text, organisation, (start, end) => ({
        kind: 'ORG',
        text: organisation,
        start,
        end,
        tier: 'strong',
      })),
    )
  }

  // A town on its own: `Standort Murten`, `am Standort Lyss`. The one inside a full
  // address loses to it in resolveOverlaps, which is right -- that is an address, not a
  // separate place. Informational by default, because whether a bare town name is
  // personal data is a policy question and not this harness's to answer; move LOCATION
  // into `redact` in policy.json to score it.
  for (const town of gazetteer.towns ?? []) {
    candidates.push(...occurrences(text, town, (start, end) => ({ kind: 'LOCATION', text: town, start, end, tier: 'strong' })))
  }

  // The weak tier last, so resolveOverlaps drops any bare surname that a full name or an
  // address already covers. `Nyffeler klärt intern` survives; the `Nyffeler` inside
  // `Monika Nyffeler` does not.
  for (const bare of [...gazetteer.lastNames, ...gazetteer.firstNames]) {
    candidates.push(...occurrences(text, bare, (start, end) => ({ kind: 'PERSON', text: bare, start, end, tier: 'weak' })))
  }

  const keep = new Set(policy.keep)
  return resolveOverlaps(candidates).map((entity, index) => ({
    ...entity,
    index,
    policy: keep.has(entity.text)
      ? 'keep'
      : policy.redact.has(entity.kind)
        ? 'redact'
        : policy.informational.has(entity.kind)
          ? 'informational'
          : 'informational',
    line: text.slice(0, entity.start).split('\n').length,
  }))
}

/**
 * Ground truth for the whole corpus, plus the counts a report leads with.
 *
 * @param overrides  optional Map of document id -> hand-corrected entity list, which is
 *                   used instead of the derivation for those documents. A correction is
 *                   only worth making if it sticks.
 */
export function buildGroundTruth(documents, gazetteer, policy, overrides = new Map()) {
  const byDocument = new Map()
  const totals = { redact: 0, keep: 0, informational: 0, strong: 0, weak: 0, byKind: {}, corrected: 0 }

  for (const document of documents) {
    const corrected = overrides.get(document.id)
    if (corrected) totals.corrected += 1
    const entities = corrected ?? groundTruthFor(document, gazetteer, policy)
    byDocument.set(document.id, entities)
    for (const entity of entities) {
      totals[entity.policy] += 1
      totals[entity.tier] += 1
      const key = `${entity.kind}${entity.tier === 'weak' ? ' (weak)' : ''}`
      totals.byKind[key] = (totals.byKind[key] ?? 0) + 1
    }
  }

  return { byDocument, totals }
}
