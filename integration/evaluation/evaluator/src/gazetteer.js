/**
 * Who and what the corpus is about, worked out from the corpus itself.
 *
 * Names cannot be found by shape. `Werner Müller` and `Guten Tag` are the same two
 * capitalised words as far as a regex is concerned, so a regex alone would put half the
 * German language into the ground truth and the precision figure would be a fiction.
 *
 * So a candidate is only accepted once some structure in the corpus vouches for it:
 * it sits in the person column of the contact list, in front of an angle-bracketed
 * address, after a chat timestamp, on a `Von:`/`An:` line -- or its normalised form is
 * the local part of an address that appears somewhere in the corpus, which is the
 * strongest evidence of all because nobody writes `guten.tag@` anywhere.
 *
 * Everything here is derived, never hand-listed, so the corpus can grow or be replaced
 * without anyone maintaining a dictionary alongside it.
 */

/** A capitalised word that could be part of a Swiss name. Hyphens and apostrophes count. */
const WORD = "[A-ZÄÖÜ][a-zäöüéèàçëïâêîôû]+(?:[-'][A-ZÄÖÜ]?[a-zäöüéèàçëïâêîôû]+)*"
const FULL_NAME = new RegExp(`\\b(${WORD}) (${WORD})\\b`, 'g')

/** Local parts are ASCII, names are not. This is the mapping every Swiss mail system uses. */
const FOLD = [
  [/ä/g, 'ae'],
  [/ö/g, 'oe'],
  [/ü/g, 'ue'],
  [/é|è|ê|ë/g, 'e'],
  [/à|â/g, 'a'],
  [/ç/g, 'c'],
  [/î|ï/g, 'i'],
  [/ô/g, 'o'],
  [/û/g, 'u'],
]

/** `Käthi Bürki` -> `kaethi.buerki`, the shape a local part would have. */
export function foldToLocalPart(name) {
  let folded = name.toLowerCase()
  for (const [pattern, replacement] of FOLD) folded = folded.replace(pattern, replacement)
  return folded.replace(/['\s]+/g, '.').replace(/[^a-z.-]/g, '')
}

/**
 * Contexts that vouch for a name on their own, because only a name can appear there.
 * Each pattern captures the name in group 1.
 */
const VOUCHERS = [
  // Sandra Bieri <sandra.bieri@natron.io>
  new RegExp(`(${WORD} ${WORD})\\s*<[^>]+@`, 'g'),
  // [08:31] Livia Marti: hat jemand ...
  new RegExp(`^\\[\\d{1,2}:\\d{2}\\]\\s+(${WORD} ${WORD})\\s*:`, 'gm'),
  // Von: / An: / Auftraggeber: / Meldende Person: / Ansprechperson ist ...
  new RegExp(
    `^(?:Von|An|Cc|Auftraggeber|Meldende Person|Ansprechperson|Bearbeitung|Zugewiesen an|Verantwortlich)\\s*:\\s*(${WORD} ${WORD})`,
    'gm',
  ),
  // --- Kommentar Sandra Bieri, 14:31 ---
  new RegExp(`Kommentar\\s+(?:von\\s+)?(${WORD} ${WORD})`, 'g'),
  // Teilnehmende: Monika Nyffeler (IT-Leitung, ...), Reto Hofer (Netzwerk)
  new RegExp(`(${WORD} ${WORD})\\s*\\((?:IT-Leitung|Netzwerk|Betriebsleitung|HR|Finanzen|Buchhaltung|Einkauf|Gesch)`, 'g'),
  // | Selhofen Immobilien GmbH | Stefan Häberli | Qualitätssicherung | ...
  new RegExp(`^\\|[^|]+\\|\\s*(${WORD} ${WORD})\\s*\\|`, 'gm'),
  // Erstkontakt column: Werner Müller (+41 79 566 86 75)
  new RegExp(`(${WORD} ${WORD})\\s*\\(\\+41`, 'g'),
]

/**
 * A town is whatever follows a four-digit postcode. That is the one place in this corpus
 * where a place name is unambiguous, and it is enough: a town named once inside a full
 * address can then be recognised everywhere else it appears on its own.
 *
 * Worth doing because `Standort Murten` and `am Standort Lyss` are all over the tickets
 * and the chat logs, with no street in front of them. Without this they are in no
 * category at all, and a proxy that hides them -- which is defensible, a bare town is
 * location data -- gets counted as having rewritten something nobody asked about.
 */
const TOWN_AFTER_POSTCODE =
  /,\s*\d{4}\s+([A-ZÄÖÜ][\wäöüéèà]+(?:\/[A-ZÄÖÜ][\wäöüéèà]+)?(?:\s+bei\s+[A-ZÄÖÜ][\wäöüéèà]+)?)/g

const ORG_SUFFIX = '(?:AG|GmbH|Genossenschaft)'
const ORG = new RegExp(`\\b(${WORD}(?: ${WORD}| [A-ZÄÖÜ][a-zäöüß]+)*? ${ORG_SUFFIX})\\b`, 'g')

function collect(pattern, text, into) {
  pattern.lastIndex = 0
  for (let match; (match = pattern.exec(text)) !== null; ) into.add(match[1].trim())
}

/**
 * Builds the gazetteer over the whole corpus at once. A name vouched for in one document
 * counts as a name in every document -- which is the point of a shared corpus, and the
 * only way `Nyffeler war zufrieden` in one file can be tied to a person introduced in
 * another.
 *
 * @param documents  [{ id, text }]
 */
export function buildGazetteer(documents) {
  const all = documents.map((document) => document.text).join('\n')

  // Every address in the corpus, as the local parts they carry. This is the evidence
  // that turns a capitalised pair into a person.
  const localParts = new Set()
  for (const match of all.matchAll(/\b([\w.+-]+)@[\w-]+\.[a-z]{2,}\b/gi)) {
    localParts.add(match[1].toLowerCase())
  }

  const vouched = new Set()
  for (const voucher of VOUCHERS) collect(voucher, all, vouched)

  // Now sweep every capitalised pair in the corpus and keep the ones the evidence covers.
  const people = new Set(vouched)
  FULL_NAME.lastIndex = 0
  for (let match; (match = FULL_NAME.exec(all)) !== null; ) {
    const candidate = `${match[1]} ${match[2]}`
    if (people.has(candidate)) continue
    if (localParts.has(foldToLocalPart(candidate))) people.add(candidate)
  }

  const organisations = new Set()
  collect(ORG, all, organisations)
  // The pattern reaches left as far as capitalised words go, so a heading like
  // "Störung Jolimont Metallbau GmbH" comes back whole. Where the corpus also uses the
  // shorter form on its own, that shorter form is the name and the longer one is a
  // sentence that happens to end with it.
  for (const candidate of [...organisations]) {
    for (const other of organisations) {
      if (other.length < candidate.length && candidate.endsWith(` ${other}`)) {
        organisations.delete(candidate)
        break
      }
    }
  }

  // A name is only ever two words here, so first and last split cleanly. These drive the
  // weak tier: `Nyffeler klärt intern` and `Guten Tag Werner` are real references to a
  // person, and a detector that misses them is worth knowing about -- but they are not
  // the headline number, because a bare surname is a genuinely harder problem.
  const firstNames = new Set()
  const lastNames = new Set()
  for (const person of people) {
    const [first, ...rest] = person.split(' ')
    firstNames.add(first)
    lastNames.add(rest.join(' '))
  }

  // A word that is both a first name and an organisation's first word (`Selhofen`,
  // `Chasseral`) would make every company mention a person. Places are not people.
  const orgWords = new Set()
  for (const organisation of organisations) {
    for (const word of organisation.split(' ')) orgWords.add(word)
  }
  for (const word of orgWords) {
    // Only drop it from the weak tier: `Peter Meier` stays a person even though `Meier`
    // is nobody's company. This only removes bare-word matches.
    firstNames.delete(word)
    lastNames.delete(word)
  }

  const towns = new Set()
  collect(TOWN_AFTER_POSTCODE, all, towns)
  // `Wohlen bei Bern` yields `Bern` as well through the shorter alternative, which is
  // wanted: both forms occur on their own in the corpus.
  for (const town of [...towns]) {
    const tail = town.match(/\sbei\s(.+)$/)
    if (tail) towns.add(tail[1])
  }

  return {
    people: [...people].sort(),
    organisations: [...organisations].sort(),
    towns: [...towns].sort((a, b) => b.length - a.length),
    firstNames: [...firstNames].sort(),
    lastNames: [...lastNames].sort(),
  }
}
