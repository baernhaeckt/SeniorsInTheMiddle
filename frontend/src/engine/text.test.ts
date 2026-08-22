import { describe, expect, it } from 'vitest'
import type { Entity } from '../protocol/types'
import { clockOf, excerptAround, formatBytes, prettyBody, splitByValues, typeTag } from './text'

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
