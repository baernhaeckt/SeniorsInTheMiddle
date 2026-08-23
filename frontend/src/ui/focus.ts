/**
 * Inspector focus: the same dashboard with the floor, and the inspector in it,
 * given most of the room.
 *
 * The desk layout splits the height roughly evenly between the band and the
 * floor, which leaves the payload matrix a few lines tall. When someone is
 * reading payloads rather than watching the band, they flip this and the
 * proportions swap. Everything else stays where it is so the eye does not
 * have to re-find it.
 *
 * One attribute on the root element, set before React mounts (see `main.tsx`),
 * so a reload into the mode never shows the desk layout first. The rules live
 * in `styles/layout.css`.
 */

import { useCallback, useEffect, useState } from 'react'

export const FOCUS_KEY = 'sitm.focus.v1'
export const FOCUS_ATTRIBUTE = 'data-focus'

/** Anything other than the stored "inspector" counts as off, including nothing stored. */
export function loadFocus(storage: Pick<Storage, 'getItem'> = window.localStorage): boolean {
  try {
    return storage.getItem(FOCUS_KEY) === 'inspector'
  } catch {
    return false
  }
}

export function saveFocus(
  on: boolean,
  storage: Pick<Storage, 'setItem'> = window.localStorage,
): void {
  try {
    storage.setItem(FOCUS_KEY, on ? 'inspector' : 'off')
  } catch {
    // The mode still holds for this session; it just will not survive a reload.
  }
}

/** Off removes the attribute rather than setting it to "off", so the CSS has one thing to match. */
export function applyFocus(on: boolean, root: Element = document.documentElement): void {
  if (on) {
    root.setAttribute(FOCUS_ATTRIBUTE, 'inspector')
  } else {
    root.removeAttribute(FOCUS_ATTRIBUTE)
  }
}

/** The mode as React state, kept in step with the root attribute and localStorage. */
export function useInspectorFocus(): readonly [boolean, () => void] {
  const [on, setOn] = useState(() => loadFocus())

  useEffect(() => {
    applyFocus(on)
    saveFocus(on)
  }, [on])

  const toggle = useCallback(() => {
    setOn((previous) => !previous)
  }, [])

  return [on, toggle] as const
}
