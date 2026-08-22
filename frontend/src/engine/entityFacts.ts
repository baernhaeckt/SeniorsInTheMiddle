import type { Entity } from '../protocol/types'
const RISK_WORDS = ['', 'not identifiable', 'semi-identifiable', 'identifiable']

/** What the detector knows about the chip, for the tooltip. */
export function chipTitle(entity: Entity): string {
  return [
    entity.informationType || entity.kind,
    entity.informationType ? entity.kind : '',
    `${Math.round(entity.confidence * 100)}% confidence`,
    RISK_WORDS[entity.riskLevel] ?? '',
    isPhi(entity.hipaaCategory) ? 'health data' : '',
  ]
    .filter(Boolean)
    .join(' · ')
}

/** The detector's own words are long; the mark is one plus sign. */
export function isPhi(hipaaCategory: string): boolean {
  const lower = hipaaCategory.toLowerCase()
  return lower === 'phi' || (lower.startsWith('protected') && !lower.startsWith('not'))
}
