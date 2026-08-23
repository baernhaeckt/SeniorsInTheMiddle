/**
 * What happened to one document on its way through the proxy.
 *
 * Three copies of the same text are compared:
 *
 *   sent       the markdown as it left this process
 *   received   the same body as it arrived at the destination host, read back from the
 *              receiver directly rather than through the proxy
 *   returned   what came back to this process, after the proxy put the real values back
 *
 * sent vs received is the redaction. sent vs returned is the restoration, and should be
 * empty: a client that asked a question about Werner Müller must get an answer about
 * Werner Müller, or the feature is a data-loss bug with a privacy story attached.
 */
import { diffText, replacements } from './diff.js'

/** Which replaced spans an entity sits in, if any. */
function overlapping(entity, spans) {
  return spans.filter((span) => entity.start < span.aEnd && span.aStart < entity.end)
}

/** How much of an entity a set of spans covers, in characters. */
function coveredLength(entity, spans) {
  let covered = 0
  for (const span of spans) {
    covered += Math.min(entity.end, span.aEnd) - Math.max(entity.start, span.aStart)
  }
  return covered
}

/** The pieces of an entity the proxy left alone, in order. */
function survivingFragments(entity, spans, sent) {
  const covering = spans
    .map((span) => [Math.max(entity.start, span.aStart), Math.min(entity.end, span.aEnd)])
    .filter(([start, end]) => end > start)
    .sort((a, b) => a[0] - b[0])

  const fragments = []
  let cursor = entity.start
  for (const [start, end] of covering) {
    if (start > cursor) fragments.push(sent.slice(cursor, start))
    cursor = Math.max(cursor, end)
  }
  if (cursor < entity.end) fragments.push(sent.slice(cursor, entity.end))
  return fragments.map((fragment) => fragment.trim()).filter(Boolean)
}

/**
 * Whether what the proxy left behind still says something about the person.
 *
 * Two ways a remnant can matter, and a fragment has to satisfy one of them:
 *
 *   a word of four letters or more.  `Beat`, `Bern`, `Freiburgstrasse` -- the parts of a
 *   value a reader recognises. This discards `+41`, `CH`, `Dr` and a check digit, which
 *   every Swiss number and account number shares with its own replacement and which
 *   identify nobody.
 *
 *   or most of the value.  Half an AHV number is half an AHV number even though it has no
 *   letters in it, so a fragment covering a large share of the original counts whatever it
 *   is made of.
 *
 * The share test is what keeps the letter test honest, and the letter test is what stops
 * the share test punishing an address. `Sulgenrainweg 80, 3250 Lyss` comes back with the
 * street replaced and the town replaced and `80, 3250` still standing: a house number and
 * a postcode, a quarter of the string, no word in it. Both identifying parts are gone, so
 * that is a value the proxy hid -- calling it half-hidden would report a working
 * substitution as a fault, which is how fifty-two of fifty-five addresses came to look
 * like near-misses when they were not.
 *
 * What survived is still recorded on the finding either way, and still shown. The
 * judgement here decides what the headline calls it, not what the reader gets to see.
 */
const SHARE_THAT_MATTERS = 0.4

function identifying(fragment, entityLength) {
  if (/\p{L}{4,}/u.test(fragment)) return true
  return fragment.replace(/\s/g, '').length / Math.max(1, entityLength) >= SHARE_THAT_MATTERS
}

/**
 * Classifies one substitution by what it landed on.
 *
 * `unclassified` is not the same as wrong. It is a value the proxy chose to hide that
 * this harness has no opinion about -- possibly personal data the ground truth missed,
 * possibly a company name split off from an entity next to it. It is counted apart from
 * both the hits and the misses precisely because calling it either would be a guess.
 */
function classify(replacement, entities) {
  // A substitution that is longer than the value it replaces comes out of the word diff
  // as one replacement plus one insertion -- `Zbinden` -> `Beat Zbinden` matches the
  // surname to itself and inserts the first name in front of it. That insertion has zero
  // width on the original side, so it overlaps nothing by the usual test, and counting it
  // as a substitution the ground truth does not cover would be wrong twice over: it
  // depresses precision and it points at the entity right next to it.
  const inserted = replacement.aStart === replacement.aEnd
  const touched = entities.filter((entity) =>
    inserted
      ? entity.start <= replacement.aStart && replacement.aStart <= entity.end
      : entity.start < replacement.aEnd && replacement.aStart < entity.end,
  )
  if (touched.some((entity) => entity.policy === 'redact')) return { verdict: 'expected', touched }
  if (touched.some((entity) => entity.policy === 'keep')) return { verdict: 'over-redaction', touched }
  if (touched.length > 0) return { verdict: 'collateral', touched }
  return { verdict: 'unclassified', touched }
}

/**
 * @param sent      the markdown that left this process
 * @param received  the same body as the destination host saw it, or null if not captured
 * @param returned  the markdown as it came back, or null if the response was unusable
 * @param entities  ground truth for this document
 */
export function analyse({ sent, received, returned, entities }) {
  const redactable = entities.filter((entity) => entity.policy === 'redact')

  // ---- outbound: what the destination host was allowed to see -------------------
  const outbound = received === null ? [] : replacements(sent, received, diffText(sent, received))

  const findings = entities.map((entity) => {
    const spans = overlapping(entity, outbound)
    const covered = coveredLength(entity, spans)
    const remnants = spans.length === 0 ? [] : survivingFragments(entity, spans, sent)
    const survived = remnants.filter((fragment) => identifying(fragment, entity.end - entity.start))
    const outcome =
      received === null ? 'unknown' : spans.length === 0 ? 'leaked' : survived.length > 0 ? 'partial' : 'replaced'

    return {
      ...entity,
      outcome,
      // What the destination host saw in its place. Several spans when a word diff broke
      // an address in two; joined with a space so the report reads as one value.
      substitute: spans.map((span) => span.after).join(' ') || null,
      survived,
      // Everything left standing, including the house numbers and check digits that did
      // not sway the verdict. Shown in the detail pane, so the judgement above can be
      // checked rather than taken on trust.
      remnants,
      coveredChars: covered,
    }
  })

  const classified = outbound.map((replacement) => ({ ...replacement, ...classify(replacement, entities) }))

  // ---- inbound: whether the client got its own data back -------------------------
  const inbound = returned === null ? [] : replacements(sent, returned, diffText(sent, returned))
  const restoredById = new Map()
  for (const entity of entities) {
    const damaged = overlapping(entity, inbound)
    restoredById.set(entity.index, returned === null ? 'unknown' : damaged.length === 0 ? 'restored' : 'damaged')
  }
  for (const finding of findings) finding.restoration = restoredById.get(finding.index)

  const of = (predicate) => findings.filter(predicate)
  const leaked = of((f) => f.policy === 'redact' && f.outcome === 'leaked')
  const partial = of((f) => f.policy === 'redact' && f.outcome === 'partial')
  const replaced = of((f) => f.policy === 'redact' && f.outcome === 'replaced')
  const overRedacted = of((f) => f.policy === 'keep' && f.outcome !== 'leaked' && f.outcome !== 'unknown')
  const damaged = of((f) => f.policy === 'redact' && f.restoration === 'damaged')

  const strong = redactable.filter((entity) => entity.tier === 'strong').length
  const strongHidden = [...replaced, ...partial].filter((finding) => finding.tier === 'strong').length
  const weak = redactable.filter((entity) => entity.tier === 'weak').length
  const weakHidden = [...replaced, ...partial].filter((finding) => finding.tier === 'weak').length

  const byKind = {}
  for (const finding of findings) {
    if (finding.policy !== 'redact') continue
    const bucket = (byKind[finding.kind] ??= { total: 0, replaced: 0, partial: 0, leaked: 0, damaged: 0 })
    bucket.total += 1
    if (finding.outcome === 'replaced') bucket.replaced += 1
    if (finding.outcome === 'partial') bucket.partial += 1
    if (finding.outcome === 'leaked') bucket.leaked += 1
    if (finding.restoration === 'damaged') bucket.damaged += 1
  }

  const verdicts = { expected: 0, 'over-redaction': 0, collateral: 0, unclassified: 0 }
  for (const replacement of classified) verdicts[replacement.verdict] += 1

  return {
    findings,
    replacements: classified,
    restorationFailures: inbound,
    byKind,
    counts: {
      entities: entities.length,
      redactable: redactable.length,
      replaced: replaced.length,
      partial: partial.length,
      leaked: leaked.length,
      overRedacted: overRedacted.length,
      damaged: damaged.length,
      substitutions: classified.length,
      ...verdicts,
      strong,
      strongHidden,
      weak,
      weakHidden,
    },
    /**
     * The three questions, per document. `clean` means the destination saw none of the
     * protected values, `intact` means the client got all of them back, `faithful`
     * means nothing that had to survive the trip was rewritten on the way.
     */
    clean: received !== null && leaked.length === 0,
    intact: returned !== null && damaged.length === 0,
    faithful: received !== null && overRedacted.length === 0,
  }
}
