import { act, renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it } from 'vitest'
import {
  PROJECTOR_ATTRIBUTE,
  PROJECTOR_KEY,
  applyProjector,
  loadProjector,
  saveProjector,
  useProjector,
} from './projector'

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

describe('loadProjector', () => {
  it('is off with nothing stored', () => {
    expect(loadProjector(memoryStorage())).toBe(false)
  })

  it('is on for the stored mode', () => {
    expect(loadProjector(memoryStorage({ [PROJECTOR_KEY]: 'on' }))).toBe(true)
  })

  it('treats anything else as off', () => {
    expect(loadProjector(memoryStorage({ [PROJECTOR_KEY]: 'off' }))).toBe(false)
    expect(loadProjector(memoryStorage({ [PROJECTOR_KEY]: 'yes' }))).toBe(false)
  })

  it('is off when storage throws', () => {
    expect(
      loadProjector({
        getItem() {
          throw new Error('blocked')
        },
      }),
    ).toBe(false)
  })
})

describe('saveProjector', () => {
  it('round-trips both ways', () => {
    const storage = memoryStorage()
    saveProjector(true, storage)
    expect(storage.read(PROJECTOR_KEY)).toBe('on')
    saveProjector(false, storage)
    expect(storage.read(PROJECTOR_KEY)).toBe('off')
  })

  it('survives storage being switched off', () => {
    expect(() => {
      saveProjector(true, {
        setItem() {
          throw new Error('blocked')
        },
      })
    }).not.toThrow()
  })
})

describe('applyProjector', () => {
  it('removes the attribute rather than setting it to off', () => {
    const root = document.createElement('div')

    applyProjector(true, root)
    expect(root.getAttribute(PROJECTOR_ATTRIBUTE)).toBe('on')

    applyProjector(false, root)
    expect(root.hasAttribute(PROJECTOR_ATTRIBUTE)).toBe(false)
  })
})

describe('useProjector', () => {
  beforeEach(() => {
    window.localStorage.clear()
    document.documentElement.removeAttribute(PROJECTOR_ATTRIBUTE)
  })

  it('starts from what was stored', () => {
    window.localStorage.setItem(PROJECTOR_KEY, 'on')
    const { result } = renderHook(() => useProjector())

    expect(result.current[0]).toBe(true)
    expect(document.documentElement.getAttribute(PROJECTOR_ATTRIBUTE)).toBe('on')
  })

  it('toggles the root attribute and remembers it', () => {
    const { result } = renderHook(() => useProjector())
    expect(result.current[0]).toBe(false)

    act(() => {
      result.current[1]()
    })
    expect(result.current[0]).toBe(true)
    expect(document.documentElement.getAttribute(PROJECTOR_ATTRIBUTE)).toBe('on')
    expect(window.localStorage.getItem(PROJECTOR_KEY)).toBe('on')

    act(() => {
      result.current[1]()
    })
    expect(result.current[0]).toBe(false)
    expect(document.documentElement.hasAttribute(PROJECTOR_ATTRIBUTE)).toBe(false)
    expect(window.localStorage.getItem(PROJECTOR_KEY)).toBe('off')
  })
})
