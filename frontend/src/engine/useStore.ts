import { useRef, useSyncExternalStore } from 'react'
import { store as appStore, type AppState, type Store } from './store'

export function shallowEqual(a: unknown, b: unknown): boolean {
  if (Object.is(a, b)) return true
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false
  const keysA = Object.keys(a)
  const keysB = Object.keys(b)
  if (keysA.length !== keysB.length) return false
  const recordB = b as Record<string, unknown>
  return keysA.every(
    (key) =>
      Object.prototype.hasOwnProperty.call(recordB, key) &&
      Object.is((a as Record<string, unknown>)[key], recordB[key]),
  )
}

interface Cache<T> {
  state: AppState
  selector: (state: AppState) => T
  value: T
}

/**
 * Subscribe to a slice of the store. The component re-renders only when the
 * selected value changes, compared shallowly, so a selector may return a
 * fresh object literal each time without causing a render loop.
 *
 * `useStore((s) => s.vault)` - a primitive or stored reference
 * `useStore((s) => ({ a: s.a, b: s.b }))` - a picked object, shallow-compared
 */
export function useStore<T>(
  selector: (state: AppState) => T,
  equal: (a: T, b: T) => boolean = shallowEqual,
  store: Store = appStore,
): T {
  const cache = useRef<Cache<T> | null>(null)

  const read = () => {
    const state = store.getSnapshot()
    const hit = cache.current
    if (hit?.state === state && hit.selector === selector) return hit.value
    const next = selector(state)
    if (hit && equal(hit.value, next)) {
      cache.current = { state, selector, value: hit.value }
      return hit.value
    }
    cache.current = { state, selector, value: next }
    return next
  }

  return useSyncExternalStore(store.subscribe, read, read)
}
