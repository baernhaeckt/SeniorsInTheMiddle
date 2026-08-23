/**
 * Where one text was changed into another, in character offsets on both sides.
 *
 * This is the whole measurement. The proxy does not splice `[PERSON_1]` into a body --
 * it substitutes a plausible stand-in drawn by a faker, so `Werner Müller` leaves as
 * `Beat Zbinden` and nothing about the receiver's copy looks redacted. Searching it for
 * PII shapes therefore finds a full set of them and tells you nothing.
 *
 * What does tell you something is the alignment: which spans of the document the proxy
 * touched. Overlay the expected entities on that and every question the harness asks has
 * an answer -- an entity inside a changed span was hidden, one inside an unchanged span
 * leaked, and a changed span covering no entity at all is the proxy rewriting something
 * it had no business rewriting.
 *
 * Lines first, then words inside the lines that differ. A whole-document character diff
 * on a 13 kB contact list is a 169-million-cell table; a line diff over its ninety-odd
 * lines followed by word diffs on the handful that changed is the same answer in a
 * millisecond.
 */

/** Longest common subsequence of two arrays, as index pairs, via the usual table. */
function lcs(a, b, same) {
  const rows = a.length
  const columns = b.length

  // One row at a time is enough for the lengths, but the pairs need a walk back through
  // the whole table. These inputs are lines or words in a line, so the table is small.
  const table = Array.from({ length: rows + 1 }, () => new Uint32Array(columns + 1))
  for (let row = rows - 1; row >= 0; row--) {
    for (let column = columns - 1; column >= 0; column--) {
      table[row][column] = same(a[row], b[column])
        ? table[row + 1][column + 1] + 1
        : Math.max(table[row + 1][column], table[row][column + 1])
    }
  }

  const pairs = []
  let row = 0
  let column = 0
  while (row < rows && column < columns) {
    if (same(a[row], b[column])) {
      pairs.push([row, column])
      row++
      column++
    } else if (table[row + 1][column] >= table[row][column + 1]) row++
    else column++
  }
  return pairs
}

/** Splits into pieces that carry their own offset, so the diff can report absolute spans. */
function slice(text, pattern) {
  const pieces = []
  let offset = 0
  for (const piece of text.split(pattern)) {
    if (piece.length > 0) pieces.push({ text: piece, start: offset, end: offset + piece.length })
    offset += piece.length
  }
  return pieces
}

const byText = (a, b) => a.text === b.text

/**
 * Turns matched index pairs into alternating equal and replaced regions covering both
 * texts completely. A region with an empty side is an insertion or a deletion; both are
 * reported as `replace`, because for this harness "the proxy changed something here" is
 * the only distinction that matters.
 */
function regionsFrom(left, right, pairs, leftStart, leftEnd, rightStart, rightEnd) {
  const regions = []
  let leftIndex = 0
  let rightIndex = 0

  const spanOf = (pieces, from, to, fallbackStart, fallbackEnd) =>
    from < to
      ? { start: pieces[from].start, end: pieces[to - 1].end }
      : { start: from > 0 ? pieces[from - 1].end : fallbackStart, end: from > 0 ? pieces[from - 1].end : fallbackEnd }

  const push = (type, leftTo, rightTo) => {
    if (leftIndex === leftTo && rightIndex === rightTo) return
    const a = spanOf(left, leftIndex, leftTo, leftStart, leftStart)
    const b = spanOf(right, rightIndex, rightTo, rightStart, rightStart)
    regions.push({ type, aStart: a.start, aEnd: a.end, bStart: b.start, bEnd: b.end })
    leftIndex = leftTo
    rightIndex = rightTo
  }

  for (const [leftAt, rightAt] of pairs) {
    push('replace', leftAt, rightAt)
    push('equal', leftAt + 1, rightAt + 1)
  }
  push('replace', left.length, right.length)

  // Nothing matched at all: the two texts share no line or word, so the whole thing is
  // one replacement. Without this the caller gets an empty region list for a body the
  // proxy rewrote from end to end.
  if (regions.length === 0) {
    regions.push({ type: 'replace', aStart: leftStart, aEnd: leftEnd, bStart: rightStart, bEnd: rightEnd })
  }
  return regions
}

/** Merges neighbouring regions of the same type, so a span is reported once and whole. */
function coalesce(regions) {
  const merged = []
  for (const region of regions) {
    const last = merged.at(-1)
    if (last && last.type === region.type && last.aEnd === region.aStart && last.bEnd === region.bStart) {
      last.aEnd = region.aEnd
      last.bEnd = region.bEnd
    } else merged.push({ ...region })
  }
  return merged
}

/** Word-level diff of two single lines, offsets relative to the whole document. */
function diffLine(a, b, aOffset, bOffset) {
  const left = slice(a, /(\s+)/).map((piece) => ({ ...piece, start: piece.start + aOffset, end: piece.end + aOffset }))
  const right = slice(b, /(\s+)/).map((piece) => ({ ...piece, start: piece.start + bOffset, end: piece.end + bOffset }))
  return regionsFrom(left, right, lcs(left, right, byText), aOffset, aOffset + a.length, bOffset, bOffset + b.length)
}

/**
 * Diffs two documents.
 *
 * @returns [{ type: 'equal' | 'replace', aStart, aEnd, bStart, bEnd }] covering both
 *          texts in order, offsets end-exclusive.
 */
export function diffText(a, b) {
  if (a === b) return [{ type: 'equal', aStart: 0, aEnd: a.length, bStart: 0, bEnd: b.length }]

  const left = slice(a, /(\n)/)
  const right = slice(b, /(\n)/)
  const blocks = regionsFrom(left, right, lcs(left, right, byText), 0, a.length, 0, b.length)

  const refined = []
  for (const block of blocks) {
    if (block.type === 'equal') {
      refined.push(block)
      continue
    }

    // A changed block of one line against one line is the common case by far: a name
    // swapped inside a sentence. Anything else -- lines added, lines dropped, a table
    // reflowed -- is reported whole rather than guessed at.
    const aLines = a.slice(block.aStart, block.aEnd).split('\n')
    const bLines = b.slice(block.bStart, block.bEnd).split('\n')
    if (aLines.length !== bLines.length) {
      refined.push(block)
      continue
    }

    let aOffset = block.aStart
    let bOffset = block.bStart
    for (let index = 0; index < aLines.length; index++) {
      if (aLines[index] !== bLines[index]) {
        refined.push(...diffLine(aLines[index], bLines[index], aOffset, bOffset))
      } else {
        refined.push({
          type: 'equal',
          aStart: aOffset,
          aEnd: aOffset + aLines[index].length,
          bStart: bOffset,
          bEnd: bOffset + bLines[index].length,
        })
      }
      aOffset += aLines[index].length + 1
      bOffset += bLines[index].length + 1
      if (index < aLines.length - 1) {
        refined.push({ type: 'equal', aStart: aOffset - 1, aEnd: aOffset, bStart: bOffset - 1, bEnd: bOffset })
      }
    }
  }

  return coalesce(refined)
}

/**
 * The replaced spans only, with the text on each side and the surrounding whitespace
 * trimmed off -- a word diff aligns on whitespace, so a substitution routinely comes out
 * as " Werner Müller" against " Beat Zbinden" and the leading space is noise in a report.
 */
export function replacements(a, b, regions) {
  return mergeAdjacent(a, b, collectReplacements(a, b, regions)).map((replacement) =>
    trimShared(a, b, replacement),
  )
}

/**
 * Punctuation that sits on both sides of a substitution belongs to the sentence, not to
 * the substitution. A word diff cannot see that, because `Müller,` is one word: it
 * reports `Werner Müller,` -> `Beat Zbinden,` and the comma travels with the name.
 */
function trimShared(a, b, replacement) {
  const trimmed = { ...replacement }
  const punctuation = /[.,;:!?)\]}"'»«]/
  const space = /\s/

  // A word diff aligns on whitespace, so merging two substitutions across a shared space
  // leaves that space inside the span. Twice: once before the punctuation is taken off
  // and once after, because `Müller ,` and `Müller,` both occur in real text.
  const edges = (test) => {
    while (trimmed.aEnd > trimmed.aStart && test(a[trimmed.aEnd - 1])) trimmed.aEnd--
    while (trimmed.bEnd > trimmed.bStart && test(b[trimmed.bEnd - 1])) trimmed.bEnd--
    while (trimmed.aEnd > trimmed.aStart && test(a[trimmed.aStart])) trimmed.aStart++
    while (trimmed.bEnd > trimmed.bStart && test(b[trimmed.bStart])) trimmed.bStart++
  }

  edges((character) => space.test(character))

  // Punctuation only comes off when it is the same on both sides: it belongs to the
  // sentence then, not to the value. `Müller,` -> `Zbinden,` is a name, not a name plus
  // a comma; `Müller.` -> `Zbinden,` is a change the report should keep showing.
  while (
    trimmed.aEnd > trimmed.aStart &&
    trimmed.bEnd > trimmed.bStart &&
    a[trimmed.aEnd - 1] === b[trimmed.bEnd - 1] &&
    punctuation.test(a[trimmed.aEnd - 1])
  ) {
    trimmed.aEnd--
    trimmed.bEnd--
  }

  edges((character) => space.test(character))

  trimmed.before = a.slice(trimmed.aStart, trimmed.aEnd)
  trimmed.after = b.slice(trimmed.bStart, trimmed.bEnd)
  return trimmed
}

/**
 * A word diff reports `Werner Müller` -> `Beat Zbinden` as two substitutions with an
 * unchanged space between them, and a phone number as four. Where the only thing between
 * two substitutions is the same run of spaces on both sides, they are one substitution
 * and reading them apart is reading them wrong. Newlines are not joined: a name at the
 * end of one line and a different value at the start of the next are two things.
 */
function mergeAdjacent(a, b, found) {
  const merged = []
  for (const replacement of found) {
    const last = merged.at(-1)
    const gapA = last ? a.slice(last.aEnd, replacement.aStart) : null
    const gapB = last ? b.slice(last.bEnd, replacement.bStart) : null
    if (last && gapA === gapB && /^[^\S\n]+$/.test(gapA)) {
      last.aEnd = replacement.aEnd
      last.bEnd = replacement.bEnd
      last.before = a.slice(last.aStart, last.aEnd)
      last.after = b.slice(last.bStart, last.bEnd)
    } else merged.push({ ...replacement })
  }
  return merged
}

function collectReplacements(a, b, regions) {
  const found = []
  for (const region of regions) {
    if (region.type !== 'replace') continue

    const before = a.slice(region.aStart, region.aEnd)
    const after = b.slice(region.bStart, region.bEnd)
    if (before.trim() === after.trim()) continue

    const leadingA = before.length - before.trimStart().length
    const leadingB = after.length - after.trimStart().length
    found.push({
      aStart: region.aStart + leadingA,
      aEnd: region.aStart + leadingA + before.trim().length,
      bStart: region.bStart + leadingB,
      bEnd: region.bStart + leadingB + after.trim().length,
      before: before.trim(),
      after: after.trim(),
    })
  }
  return found
}
