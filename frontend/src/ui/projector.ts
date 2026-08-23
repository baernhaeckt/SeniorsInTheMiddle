/**
 * Projector mode: the same dashboard, set for a room instead of a desk.
 *
 * A FullHD projector has a laptop's pixel count and none of its viewing
 * distance, so growing the type on its own would only push the dashboard off
 * the screen. The mode does both halves of that trade, and both live in
 * `styles/projector.css`: sizes collapse onto four steps that start where
 * legibility does, and the detail nobody at the back could use — timestamps,
 * byte counts, the log ticker, addresses — comes out to pay for it.
 *
 * The switch is one attribute on the root element. It is set before React
 * mounts (see `main.tsx`), so reloading into the mode never shows the desk
 * layout first.
 */

import { useCallback, useEffect, useState } from 'react'

export const PROJECTOR_KEY = 'sitm.projector.v1'
export const PROJECTOR_ATTRIBUTE = 'data-projector'

/** Anything other than the stored "on" counts as off, including nothing stored. */
export function loadProjector(storage: Pick<Storage, 'getItem'> = window.localStorage): boolean {
  try {
    return storage.getItem(PROJECTOR_KEY) === 'on'
  } catch {
    // A browser with storage switched off still runs, at desk size.
    return false
  }
}

export function saveProjector(
  on: boolean,
  storage: Pick<Storage, 'setItem'> = window.localStorage,
): void {
  try {
    storage.setItem(PROJECTOR_KEY, on ? 'on' : 'off')
  } catch {
    // The mode still holds for this session; it just will not survive a reload.
  }
}

/** Off removes the attribute rather than setting it to "off", so the CSS has one thing to match. */
export function applyProjector(on: boolean, root: Element = document.documentElement): void {
  if (on) {
    root.setAttribute(PROJECTOR_ATTRIBUTE, 'on')
  } else {
    root.removeAttribute(PROJECTOR_ATTRIBUTE)
  }
}

/**
 * The mode as React state, kept in step with the root attribute and localStorage.
 *
 * The first run writes back what it just read, which costs nothing and means the
 * attribute is right even if something else cleared it.
 */
export function useProjector(): readonly [boolean, () => void] {
  const [on, setOn] = useState(() => loadProjector())

  useEffect(() => {
    applyProjector(on)
    saveProjector(on)
  }, [on])

  const toggle = useCallback(() => {
    setOn((previous) => !previous)
  }, [])

  return [on, toggle] as const
}
