/** Below this width the band uses its narrow layout. Mirrors `--bp-narrow` in tokens.css. */
export const BREAKPOINT_NARROW = 760

/** Read once when an animation starts; the OS setting rarely changes mid-flight. */
export function prefersReducedMotion(): boolean {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches
}
