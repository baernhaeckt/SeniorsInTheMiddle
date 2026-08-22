import type { CSSProperties } from 'react'

/** Typed way to hand custom properties to `style=`; React's types stop at standard ones. */
export function cssVars(vars: Record<`--${string}`, string | number>): CSSProperties {
  return vars
}
