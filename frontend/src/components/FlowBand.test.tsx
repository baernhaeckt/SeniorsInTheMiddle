import { act, render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { store } from '../engine/store'
import treated from '../protocol/fixtures/treated-exchange.json'
import type { EventOf, ServerEvent } from '../protocol/types'
import { FlowBand } from './FlowBand'

const frames = treated as ServerEvent[]
const byType = <T extends ServerEvent['type']>(type: T): EventOf<T> => {
  const frame = frames.find((item) => item.type === type)
  if (!frame) throw new Error(`no fixture frame of type ${type}`)
  return frame as EventOf<T>
}

/** A ResizeObserver that reports a fixed band size as soon as it observes. */
class SizedResizeObserver {
  constructor(private readonly callback: ResizeObserverCallback) {}
  observe() {
    this.callback([{ contentRect: { width: 1200, height: 320 } } as ResizeObserverEntry], this)
  }
  unobserve() {}
  disconnect() {}
}

beforeEach(() => {
  vi.useFakeTimers()
  vi.stubGlobal('ResizeObserver', SizedResizeObserver)
  store.reset()
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

function openExchange(id: string) {
  const observed = byType('request.observed')
  const opened = byType('exchange.opened')
  store.apply({ ...observed, requestId: `r-${id}`, exchangeId: id })
  store.apply({ ...opened, requestId: `r-${id}`, exchangeId: id })
}

describe('FlowBand', () => {
  it('removes every ripple after its lifetime even when exchanges keep changing', () => {
    const { container } = render(<FlowBand />)
    const ripples = () => container.querySelectorAll('.wall__ripple').length

    // Each event is its own render, the way they arrive on the wire.
    act(() => {
      openExchange('a')
    })
    act(() => {
      store.apply({ ...byType('detection.completed'), exchangeId: 'a' })
    })
    act(() => {
      store.apply({ ...byType('redaction.completed'), exchangeId: 'a' })
    })
    expect(ripples()).toBe(1)

    // Another stage change well inside the first ripple's lifetime.
    act(() => {
      vi.advanceTimersByTime(300)
      openExchange('b')
    })
    act(() => {
      store.apply({ ...byType('detection.completed'), exchangeId: 'b' })
    })
    act(() => {
      store.apply({ ...byType('redaction.completed'), exchangeId: 'b' })
    })
    expect(ripples()).toBe(2)

    act(() => {
      vi.advanceTimersByTime(750)
    })
    expect(ripples()).toBe(1)
    act(() => {
      vi.advanceTimersByTime(300)
    })
    expect(ripples()).toBe(0)
  })

  it('draws a mote per untreated request and lets it go after it has crossed', () => {
    const { container } = render(<FlowBand />)
    const motes = () => container.querySelectorAll('.mote').length
    const observed = byType('request.observed')

    act(() => {
      store.apply({ ...observed, requestId: 'p1', treatment: 'passthrough' })
      store.apply({ ...observed, requestId: 'c1', treatment: 'clean' })
      store.apply({ ...observed, requestId: 't1', treatment: 'treated', exchangeId: 'x' })
    })
    expect(motes()).toBe(2)
    act(() => {
      vi.advanceTimersByTime(5_000)
    })
    expect(motes()).toBe(0)
  })

  it('names the active client and host on the nodes', () => {
    const { container } = render(<FlowBand />)
    expect(container.textContent).toContain('behind the proxy')
    act(() => {
      openExchange('a')
    })
    expect(container.textContent).toContain('Tablet · Studer')
    expect(container.textContent).toContain('api.example.ch')
  })
})
