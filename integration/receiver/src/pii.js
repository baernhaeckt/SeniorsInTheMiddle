/**
 * What counts as personal data in a payload, and what counts as a stand-in for it.
 *
 * The receiver plays a destination host: it is the party that must NOT see the real
 * values once the proxy redacts. So it looks at every body it reads and reports what it
 * found, which is the one number this whole harness exists to drive to zero.
 *
 * The kinds mirror the EntityKind union in frontend/src/protocol/types.ts, so what the
 * receiver reports lines up with what the dashboard will eventually show.
 */

/** Patterns with a shape specific enough to match without a dictionary. */
export const PII_PATTERNS = [
  ['AHV', /\b756\.\d{4}\.\d{4}\.\d{2}\b/g],
  ['IBAN', /\bCH\d{2}[ ]?\d{4}[ ]?\d{4}[ ]?\d{4}[ ]?\d{4}[ ]?\d\b/g],
  ['PHONE', /\+41[ ]?\d{2}[ ]?\d{3}[ ]?\d{2}[ ]?\d{2}\b/g],
  ['EMAIL', /\b[\w.+-]+@[\w-]+\.[a-z]{2,}\b/gi],
  ['BIRTHDATE', /\b(?:0[1-9]|[12]\d|3[01])\.(?:0[1-9]|1[0-2])\.(?:19|20)\d{2}\b/g],
]

/** The stand-in the proxy is expected to substitute, e.g. `[PERSON_1]`. */
export const TOKEN_PATTERN =
  /\[(PERSON|AHV|IBAN|ADDRESS|PHONE|EMAIL|BIRTHDATE|HEALTH|INSURANCE)_\d+\]/g

/**
 * Names cannot be found by shape, so the sender declares which ones it used in the
 * X-Harness-Names header. Trusting the sender is fine here: both ends are the harness.
 */
export function inspect(body, declaredNames = []) {
  const kinds = {}
  let rawHits = 0

  for (const [kind, pattern] of PII_PATTERNS) {
    const matches = body.match(pattern)
    if (matches?.length) {
      kinds[kind] = (kinds[kind] ?? 0) + matches.length
      rawHits += matches.length
    }
  }

  for (const name of declaredNames) {
    if (name && body.includes(name)) {
      kinds.PERSON = (kinds.PERSON ?? 0) + 1
      rawHits += 1
    }
  }

  const tokens = body.match(TOKEN_PATTERN) ?? []

  return { rawHits, kinds, tokenCount: tokens.length, sawRawPii: rawHits > 0 }
}
