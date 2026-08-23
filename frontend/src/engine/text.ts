import type { Entity } from '../protocol/types'

export interface Excerpt {
  before: string
  focus: string
  after: string
}

/**
 * A window of text centred on one identifier, so the thing that changes is
 * always the thing you are looking at.
 */
export function excerptAround(text: string, focus: string, width: number): Excerpt {
  if (!text) return { before: '', focus: '', after: '' }
  const index = focus ? text.indexOf(focus) : -1

  if (index < 0) {
    const clipped = text.slice(0, width)
    return { before: clipped + (text.length > width ? '…' : ''), focus: '', after: '' }
  }

  const pad = Math.max(0, Math.floor((width - focus.length) / 2))
  const start = Math.max(0, index - pad)
  const end = Math.min(text.length, index + focus.length + pad)

  return {
    before: (start > 0 ? '…' : '') + text.slice(start, index),
    focus,
    after: text.slice(index + focus.length, end) + (end < text.length ? '…' : ''),
  }
}

/** Split text into runs so identifiers can be marked without regex escaping. */
export interface Run {
  text: string
  entity?: Entity
}

/**
 * The raw request body, cut at the offsets the proxy reported. Exact where
 * `splitByValues` has to guess: a value that occurs twice, or inside another,
 * is marked where it was found and nowhere else. Falls back to the search when
 * an offset does not land on its value -- the body was pretty-printed, say.
 */
export function splitByOffsets(text: string, entities: Entity[]): Run[] {
  if (!text) return []
  const placed = [...entities]
    .filter((entity) => entity.end > entity.start && entity.end <= text.length)
    .sort((a, b) => a.start - b.start)

  if (placed.length === 0) return splitByValues(text, entities, false)

  const runs: Run[] = []
  let cursor = 0
  for (const entity of placed) {
    if (entity.start < cursor) continue
    if (text.slice(entity.start, entity.end) !== entity.value)
      return splitByValues(text, entities, false)
    if (entity.start > cursor) runs.push({ text: text.slice(cursor, entity.start) })
    runs.push({ text: entity.value, entity })
    cursor = entity.end
  }
  if (cursor < text.length) runs.push({ text: text.slice(cursor) })
  return runs
}

export function splitByValues(text: string, entities: Entity[], useToken: boolean): Run[] {
  if (!text) return []
  const needles = entities
    .map((entity) => ({ entity, needle: useToken ? entity.token : entity.value }))
    .filter((item) => item.needle.length > 0)

  if (needles.length === 0) return [{ text }]

  const runs: Run[] = []
  let cursor = 0

  while (cursor < text.length) {
    let bestAt = -1
    let best: (typeof needles)[number] | null = null

    for (const item of needles) {
      const at = text.indexOf(item.needle, cursor)
      if (at !== -1 && (bestAt === -1 || at < bestAt)) {
        bestAt = at
        best = item
      }
    }

    if (!best || bestAt === -1) {
      runs.push({ text: text.slice(cursor) })
      break
    }

    if (bestAt > cursor) runs.push({ text: text.slice(cursor, bestAt) })
    runs.push({ text: best.needle, entity: best.entity })
    cursor = bestAt + best.needle.length
  }

  return runs
}

export function clockOf(at: number): string {
  const date = new Date(at)
  const pad = (value: number, size = 2) => String(value).padStart(size, '0')
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}

/**
 * JSON bodies arrive minified. Lay them out for reading, but only if they
 * really parse. Leave a body we cannot parse exactly as the proxy sent it.
 */
export function prettyBody(text: string | undefined, contentType?: string): string {
  if (!text) return ''
  if (contentType && !contentType.includes('json')) return text
  try {
    return JSON.stringify(JSON.parse(text), null, 2)
  } catch {
    return text
  }
}

/** One piece of a readout line: plain text, or an identifier that can animate. */
export interface ReadoutRun {
  text: string
  /**
   * Set on identifiers only, and the same string at every stage, so React keeps
   * one component on the run as its text turns from a value into a token — a
   * remounted one has nothing to animate from.
   */
  key?: string
}

export interface ReadoutLine {
  /**
   * Named after the identifier it carries rather than its position: a body
   * changes shape between stages, and a line that shifted a row would take a
   * fresh set of runs with it.
   */
  key: string
  runs: ReadoutRun[]
}

export interface ReadoutWindow {
  /** True when the body laid out as JSON, so every line is one field. */
  structured: boolean
  /** Exactly as many entries as were asked for, padded with blanks. */
  lines: ReadoutLine[]
}

/**
 * A body as a few lines, framed on the identifiers that are about to change.
 *
 * The gate readout used to take one window of raw JSON and let the box wrap it.
 * That put the identifier wherever the wrapping happened to land — often on the
 * line under the fold, which is the one line it must never be on. Laid out, a
 * body has real lines: the one carrying the first identifier goes in the middle
 * and the neighbours give it context, so the thing that changes is always in
 * the same place and always on screen.
 *
 * Every identifier in the window is marked, not only the one the window is
 * anchored on. Four fields of a payload are visible at once here, and a screen
 * where one value churns into its token while the three beside it flick over is
 * a screen that says the churn is decoration.
 *
 * `width` bounds a line that carries an identifier, and generously — the box
 * clips what it cannot fit. It is there so a long line cannot push an
 * identifier off the right-hand edge, not to decide how much text fits.
 *
 * Bodies that are not JSON have no lines to work with, so they come back as one
 * window of `lines * width` characters for the box to wrap as before.
 */
export function readoutWindow(
  text: string,
  entities: Entity[],
  useToken: boolean,
  lines: number,
  width: number,
): ReadoutWindow {
  const needleOf = (entity: Entity) => (useToken ? entity.token : entity.value)
  const marked = entities.filter((entity) => needleOf(entity).length > 0)

  /** The line's own text, cut down to keep its first identifier in view. */
  const clip = (line: string, room: number): string => {
    const on = marked.find((entity) => line.includes(needleOf(entity)))
    if (!on) return line
    const part = excerptAround(line, needleOf(on), room)
    return part.before + part.focus + part.after
  }

  const toLine = (line: string, index: number, used: Set<string>): ReadoutLine => {
    const runs = splitByValues(line, marked, useToken).map((run) => ({
      text: run.text,
      ...(run.entity ? { key: run.entity.id } : {}),
    }))
    const named = runs.find((run) => run.key !== undefined)?.key
    // Two lines carrying the same identifier would otherwise share a key.
    const key = named !== undefined && !used.has(named) ? named : `line-${index}`
    used.add(key)
    return { key, runs }
  }

  if (!text) return { structured: false, lines: [] }

  const laidOut = prettyBody(text).split('\n')
  const used = new Set<string>()

  if (laidOut.length < 2) {
    return { structured: false, lines: [toLine(clip(text, lines * width), 0, used)] }
  }

  const anchorNeedle = marked[0] ? needleOf(marked[0]) : ''
  const found = anchorNeedle ? laidOut.findIndex((line) => line.includes(anchorNeedle)) : -1
  const anchor = Math.max(0, found)
  const first = Math.min(
    Math.max(0, anchor - Math.floor((lines - 1) / 2)),
    Math.max(0, laidOut.length - lines),
  )

  const window: ReadoutLine[] = []
  for (let index = first; index < first + lines; index += 1) {
    const line = laidOut[index]
    window.push(
      line === undefined
        ? { key: `line-${index}`, runs: [] }
        : toLine(clip(line, width), index, used),
    )
  }
  return { structured: true, lines: window }
}

export function formatBytes(bytes: number | undefined): string {
  if (bytes === undefined) return '—'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} kB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

/** Short, honest label for why a request was waved past. */
export function typeTag(contentType?: string): string {
  if (!contentType) return 'asset'
  if (contentType.includes('css')) return 'css'
  if (contentType.includes('javascript')) return 'js'
  if (contentType.startsWith('font/')) return 'font'
  if (contentType.startsWith('image/')) return 'image'
  if (contentType.includes('json')) return 'json'
  return contentType.split('/')[1] ?? 'asset'
}

/** Characters an address keeps at its end: enough for a port and a short path. */
const CLIP_TAIL = 12

export interface ClipParts {
  head: string
  tail: string
}

/**
 * Split an address so the ellipsis lands in the middle rather than at the end.
 * The tail of a URL carries the port and the path — the part someone reads to
 * check it against a device — so it is the head that gives way when the space
 * runs out. Rendered by `components/Clip.tsx`.
 */
export function clipParts(value: string, tail: number = CLIP_TAIL): ClipParts {
  if (value.length <= tail) return { head: value, tail: '' }
  const cut = value.length - tail
  return { head: value.slice(0, cut), tail: value.slice(cut) }
}
