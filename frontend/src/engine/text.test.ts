import { describe, expect, it } from 'vitest'
import type { Entity } from '../protocol/types'
import {
  clipParts,
  clockOf,
  excerptAround,
  formatBytes,
  readoutWindow,
  prettyBody,
  splitByValues,
  typeTag,
} from './text'

describe('excerptAround', () => {
  it('handles empty text', () => {
    expect(excerptAround('', 'x', 10)).toEqual({ before: '', focus: '', after: '' })
  })

  it('clips from the start when there is no focus', () => {
    expect(excerptAround('abcdefghij', '', 4)).toEqual({ before: 'abcd…', focus: '', after: '' })
    expect(excerptAround('abc', 'zzz', 10)).toEqual({ before: 'abc', focus: '', after: '' })
  })

  it('centres the window on the focus with ellipses where it clips', () => {
    const result = excerptAround('0123456789FOCUS0123456789', 'FOCUS', 11)
    expect(result.focus).toBe('FOCUS')
    expect(result.before).toBe('…789')
    expect(result.after).toBe('012…')
  })

  it('does not add ellipses at the edges of the text', () => {
    expect(excerptAround('FOCUS tail', 'FOCUS', 40)).toEqual({
      before: '',
      focus: 'FOCUS',
      after: ' tail',
    })
  })

  it('copes with a window narrower than the focus', () => {
    const result = excerptAround('aaFOCUSbb', 'FOCUS', 2)
    expect(result.focus).toBe('FOCUS')
    expect(result.before).toBe('…')
    expect(result.after).toBe('…')
  })
})

describe('splitByValues', () => {
  const entity = (value: string, token: string): Entity => ({
    id: token,
    kind: 'PERSON',
    value,
    token,
    start: 0,
    end: 0,
    confidence: 1,
    informationType: '',
    riskLevel: 0,
    hipaaCategory: '',
  })

  it('returns one run for text with no needles', () => {
    expect(splitByValues('abc', [], false)).toEqual([{ text: 'abc' }])
    expect(splitByValues('', [entity('a', '[A]')], false)).toEqual([])
  })

  it('marks values in order, including repeats', () => {
    const runs = splitByValues('x Anna y Anna', [entity('Anna', '[P]')], false)
    expect(runs.map((run) => run.text)).toEqual(['x ', 'Anna', ' y ', 'Anna'])
    expect(runs.filter((run) => run.entity)).toHaveLength(2)
  })

  it('marks tokens when asked', () => {
    const runs = splitByValues('x [P] y', [entity('Anna', '[P]')], true)
    expect(runs[1]).toMatchObject({ text: '[P]' })
  })

  it('picks the earliest match when needles compete', () => {
    const runs = splitByValues('bb aa', [entity('aa', '[A]'), entity('bb', '[B]')], false)
    expect(runs.map((run) => run.entity?.token ?? run.text)).toEqual(['[B]', ' ', '[A]'])
  })

  it('ignores empty needles', () => {
    expect(splitByValues('abc', [entity('', '[E]')], false)).toEqual([{ text: 'abc' }])
  })
})

describe('prettyBody', () => {
  it('formats JSON and leaves everything else alone', () => {
    expect(prettyBody('{"a":1}', 'application/json')).toBe('{\n  "a": 1\n}')
    expect(prettyBody('{"a":1}')).toBe('{\n  "a": 1\n}')
    expect(prettyBody('{"a":1}', 'text/plain')).toBe('{"a":1}')
    expect(prettyBody('{oops', 'application/json')).toBe('{oops')
    expect(prettyBody(undefined)).toBe('')
  })
})

describe('formatting', () => {
  it('formatBytes', () => {
    expect(formatBytes(undefined)).toBe('—')
    expect(formatBytes(12)).toBe('12 B')
    expect(formatBytes(2048)).toBe('2.0 kB')
    expect(formatBytes(3 * 1024 * 1024)).toBe('3.0 MB')
  })

  it('clockOf pads every part', () => {
    const date = new Date(2026, 0, 1, 7, 5, 9)
    expect(clockOf(date.getTime())).toBe('07:05:09')
  })

  it('clipParts keeps the end of an address, where the port and path are', () => {
    const long = 'http://backend.northeurope.azurecontainerapps.io:3128/ca.crt'
    const parts = clipParts(long)

    expect(parts.head + parts.tail).toBe(long)
    expect(parts.tail).toBe(':3128/ca.crt')
    expect(clipParts('host:3128')).toEqual({ head: 'host:3128', tail: '' })
  })

  it('typeTag', () => {
    expect(typeTag()).toBe('asset')
    expect(typeTag('text/css')).toBe('css')
    expect(typeTag('application/javascript')).toBe('js')
    expect(typeTag('font/woff2')).toBe('font')
    expect(typeTag('image/png')).toBe('image')
    expect(typeTag('application/json')).toBe('json')
    expect(typeTag('video/mp4')).toBe('mp4')
    expect(typeTag('weird')).toBe('asset')
  })
})

describe('readoutWindow', () => {
  const entity = (id: string, value: string, token: string): Entity => ({
    id,
    kind: 'PERSON',
    value,
    token,
    start: 0,
    end: 0,
    confidence: 0.9,
    informationType: '',
    riskLevel: 3,
    hipaaCategory: '',
  })

  const BODY = JSON.stringify({
    claimId: 'CLM-1',
    insuredName: 'Rosmarie Studer',
    town: 'Bern',
    policy: '42-118',
    note: 'later',
  })
  const NAME = entity('e1', 'Rosmarie Studer', '[PERSON_1]')

  it('lays a JSON body out and centres the window on the identifier', () => {
    const window = readoutWindow(BODY, [NAME], false, 3, 64)

    expect(window.structured).toBe(true)
    expect(window.lines).toHaveLength(3)
    // The identifier's line has the line before it above and the next below.
    expect(window.lines[1]?.runs.map((run) => run.text).join('')).toContain('Rosmarie Studer')
    expect(window.lines[0]?.runs.map((run) => run.text).join('')).toContain('claimId')
    expect(window.lines[2]?.runs.map((run) => run.text).join('')).toContain('town')
  })

  it('marks the identifier as its own run, keyed so it can animate', () => {
    const marked = readoutWindow(BODY, [NAME], false, 3, 64).lines[1]?.runs.filter(
      (run) => run.key !== undefined,
    )

    expect(marked).toEqual([{ text: 'Rosmarie Studer', key: 'e1' }])
  })

  it('keeps the key the same once the value has become its token', () => {
    const before = readoutWindow(BODY, [NAME], false, 3, 64)
    const after = readoutWindow(BODY.replace(NAME.value, NAME.token), [NAME], true, 3, 64)

    expect(before.lines[1]?.key).toBe(after.lines[1]?.key)
    const run = after.lines[1]?.runs.find((item) => item.key !== undefined)
    expect(run).toEqual({ text: '[PERSON_1]', key: 'e1' })
  })

  it('marks every identifier in the window, not only the one it is centred on', () => {
    const town = entity('e2', 'Bern', '[CITY_1]')
    const window = readoutWindow(BODY, [NAME, town], false, 4, 64)
    const keys = window.lines.flatMap((line) => line.runs.flatMap((run) => run.key ?? []))

    expect(keys).toEqual(['e1', 'e2'])
  })

  it('asks for as many lines as it is given, even from a short body', () => {
    expect(readoutWindow('{"a":1}', [], false, 4, 64).lines).toHaveLength(4)
  })

  it('gives a body that is not JSON back as one run of text', () => {
    const window = readoutWindow('name=Rosmarie Studer&town=Bern', [NAME], false, 4, 10)

    expect(window.structured).toBe(false)
    expect(window.lines).toHaveLength(1)
    expect(window.lines[0]?.runs.some((run) => run.key === 'e1')).toBe(true)
  })

  it('has nothing to show for an empty body', () => {
    expect(readoutWindow('', [NAME], false, 4, 64)).toEqual({ structured: false, lines: [] })
  })
})
