/**
 * The signed-in user, kept on this browser the same way the runtime config is.
 *
 * A wall display is opened once and left alone, so the token outliving a reload matters more
 * here than it would in an app someone signs into deliberately each morning. The backend
 * issues it for 48 hours; nothing renews it, so it expires and the login screen comes back.
 */

import * as v from 'valibot'

export const SessionSchema = v.object({
  /** The raw JWT, exactly as the backend issued it. */
  token: v.string(),
  username: v.string(),
  email: v.string(),
  /**
   * The API origin this token came from. A token is only meaningful to the backend that
   * signed it, so pointing the dashboard at a different proxy has to end the session rather
   * than carry it across.
   */
  origin: v.string(),
})
export type Session = v.InferOutput<typeof SessionSchema>

export const SESSION_KEY = 'sitm.session.v1'

/**
 * Read the `exp` claim without verifying anything.
 *
 * The signature is the server's business; the only question here is whether it is worth
 * opening a socket that would be refused. Returns null when the token has no readable
 * expiry, which is treated as "cannot tell" rather than "expired" — the backend is the one
 * that decides, and a token it still accepts should not be thrown away by a parsing quirk.
 */
export function expiryOf(token: string): number | null {
  const payload = token.split('.')[1]
  if (!payload) return null

  try {
    const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'))
    const claims: unknown = JSON.parse(json)
    if (!claims || typeof claims !== 'object') return null

    const exp = (claims as Record<string, unknown>).exp
    return typeof exp === 'number' ? exp * 1000 : null
  } catch {
    return null
  }
}

/** Clock skew allowance, so a token about to lapse is not opened as if it were fresh. */
const EXPIRY_MARGIN_MS = 5000

export function isExpired(token: string, now: number = Date.now()): boolean {
  const expiry = expiryOf(token)
  return expiry !== null && expiry - EXPIRY_MARGIN_MS <= now
}

/**
 * The stored session, or null when there is none worth using.
 *
 * A session for a different backend, or one whose token has run out, counts as absent: both
 * would otherwise send the dashboard into a socket that can only fail.
 */
export function loadSession(
  origin: string,
  storage: Pick<Storage, 'getItem'> = window.localStorage,
): Session | null {
  let raw: string | null
  try {
    raw = storage.getItem(SESSION_KEY)
  } catch {
    return null
  }
  if (!raw) return null

  let parsed: unknown
  try {
    parsed = JSON.parse(raw)
  } catch {
    return null
  }

  const result = v.safeParse(SessionSchema, parsed)
  if (!result.success) return null

  const session = result.output
  if (session.origin !== origin) return null
  if (isExpired(session.token)) return null

  return session
}

export function saveSession(
  session: Session,
  storage: Pick<Storage, 'setItem'> = window.localStorage,
): void {
  try {
    storage.setItem(SESSION_KEY, JSON.stringify(session))
  } catch {
    // A browser with storage switched off still runs for this session.
  }
}

export function clearSession(storage: Pick<Storage, 'removeItem'> = window.localStorage): void {
  try {
    storage.removeItem(SESSION_KEY)
  } catch {
    // Nothing was stored, so nothing needs removing.
  }
}

/**
 * The initials shown in the header bubble. Two letters at most: a name split across words
 * gives one from each, anything else gives its first character.
 */
export function initialsOf(username: string): string {
  const words = username
    .trim()
    .split(/[\s._-]+/)
    .filter(Boolean)
  if (words.length === 0) return '?'
  if (words.length === 1) return (words[0] ?? '').slice(0, 2).toUpperCase()

  return `${(words[0] ?? '').charAt(0)}${(words[1] ?? '').charAt(0)}`.toUpperCase()
}
