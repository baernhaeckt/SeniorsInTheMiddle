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
