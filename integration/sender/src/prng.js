/**
 * A seeded PRNG, so a run can be repeated. `Math.random()` would make a surprising
 * failure impossible to reproduce, which is the one thing a harness must not do.
 * mulberry32: small, fast, good enough for picking scenarios and fixtures.
 */
export function createRng(seed) {
  let state = seed >>> 0
  return function next() {
    state = (state + 0x6d2b79f5) >>> 0
    let t = state
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}
