import { describe, expect, it } from 'vitest'
import { CLEAN, PASSTHROUGH, TREATED, compileExchange } from './scenarios'

describe('compileExchange', () => {
  it.each(TREATED.map((scenario, i) => [scenario.host, i]))(
    '%s: offsets and tokens line up',
    (_, i) => {
      const scenario = TREATED[i]
      if (!scenario) throw new Error('missing scenario')
      const compiled = compileExchange(scenario, i)
      expect(compiled.entities.length).toBeGreaterThan(0)
      for (const entity of compiled.entities) {
        expect(compiled.requestBody.slice(entity.start, entity.end)).toBe(entity.value)
        expect(compiled.redactedRequestBody).toContain(entity.token)
        expect(compiled.redactedRequestBody).not.toContain(entity.value)
        expect(entity.confidence).toBeGreaterThan(0)
        expect(entity.confidence).toBeLessThanOrEqual(1)
      }
      expect(compiled.requestBody).not.toContain('{{')
      expect(compiled.tokenizedResponseBody).not.toContain('{{')
      for (const entity of compiled.entities) {
        expect(compiled.responseBody).not.toContain(entity.token)
      }
    },
  )

  it('reuses a token for a repeated value', () => {
    const base = TREATED[0]
    if (!base) throw new Error('missing scenario')
    const compiled = compileExchange(
      { ...base, request: '{{PERSON|A}} and {{PERSON|A}} and {{PERSON|B}}', response: '' },
      0,
    )
    expect(compiled.entities.map((entity) => entity.token)).toEqual([
      '[PERSON_1]',
      '[PERSON_1]',
      '[PERSON_2]',
    ])
  })

  it('ships plain samples with a reason', () => {
    for (const sample of [...PASSTHROUGH, ...CLEAN]) {
      expect(sample.reason.length).toBeGreaterThan(0)
    }
  })
})
