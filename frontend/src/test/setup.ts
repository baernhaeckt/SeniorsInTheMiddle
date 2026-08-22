import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

afterEach(() => {
  cleanup()
})

// jsdom has no matchMedia; the app only asks about reduced motion.
if (!window.matchMedia) {
  window.matchMedia = (query: string) =>
    ({
      matches: false,
      media: query,
      onchange: null,
      addEventListener() {},
      removeEventListener() {},
      addListener() {},
      removeListener() {},
      dispatchEvent: () => false,
    }) as MediaQueryList
}

if (!window.ResizeObserver) {
  window.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
}

// jsdom has no modal dialogs. Enough of one for the guide to open and close.
if (!HTMLDialogElement.prototype.showModal) {
  HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) {
    this.setAttribute('open', '')
  }
  HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
    this.removeAttribute('open')
    this.dispatchEvent(new Event('close'))
  }
}

// jsdom has no Web Animations API. A stub that finishes instantly is enough
// for the packet layer to mount.
if (!Element.prototype.animate) {
  Element.prototype.animate = function animate(this: Element) {
    const animation = {
      onfinish: null as (() => void) | null,
      cancel() {},
      effect: { getComputedTiming: () => ({ progress: 1 }) },
    }
    queueMicrotask(() => animation.onfinish?.())
    return animation as unknown as Animation
  }
}
