import { act, renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it } from 'vitest'
import {
  FOCUS_ATTRIBUTE,
  FOCUS_KEY,
  applyFocus,
  loadFocus,
  saveFocus,
  useInspectorFocus,
} from './focus'

function memoryStorage(initial: Record<string, string> = {}) {
  const data = new Map(Object.entries(initial))
  return {
    getItem: (key: string) => data.get(key) ?? null,
    setItem: (key: string, value: string) => {
      data.set(key, value)
    },
    read: (key: string) => data.get(key) ?? null,
  }
}

describe('loadFocus / saveFocus', () => {
  it('is off with nothing stored, on for the stored mode', () => {
    expect(loadFocus(memoryStorage())).toBe(false)
    expect(loadFocus(memoryStorage({ [FOCUS_KEY]: 'inspector' }))).toBe(true)
    expect(loadFocus(memoryStorage({ [FOCUS_KEY]: 'on' }))).toBe(false)
  })

  it('round-trips', () => {
    const storage = memoryStorage()
    saveFocus(true, storage)
    expect(loadFocus(storage)).toBe(true)
    saveFocus(false, storage)
    expect(loadFocus(storage)).toBe(false)
  })

  it('survives a storage that throws', () => {
    const broken = {
      getItem: () => {
        throw new Error('blocked')
      },
      setItem: () => {
        throw new Error('blocked')
      },
    }
    expect(loadFocus(broken)).toBe(false)
    expect(() => saveFocus(true, broken)).not.toThrow()
  })
})

describe('applyFocus', () => {
  it('sets and removes the root attribute', () => {
    const root = document.createElement('div')
    applyFocus(true, root)
    expect(root.getAttribute(FOCUS_ATTRIBUTE)).toBe('inspector')
    applyFocus(false, root)
    expect(root.hasAttribute(FOCUS_ATTRIBUTE)).toBe(false)
  })
})

describe('useInspectorFocus', () => {
  beforeEach(() => {
    window.localStorage.clear()
    document.documentElement.removeAttribute(FOCUS_ATTRIBUTE)
  })

  it('toggles, and keeps the attribute and storage in step', () => {
    const { result } = renderHook(() => useInspectorFocus())
    expect(result.current[0]).toBe(false)
    act(() => result.current[1]())
    expect(result.current[0]).toBe(true)
    expect(document.documentElement.getAttribute(FOCUS_ATTRIBUTE)).toBe('inspector')
    expect(window.localStorage.getItem(FOCUS_KEY)).toBe('inspector')
    act(() => result.current[1]())
    expect(document.documentElement.hasAttribute(FOCUS_ATTRIBUTE)).toBe(false)
  })
})
