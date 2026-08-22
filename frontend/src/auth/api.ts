/**
 * The dashboard's only REST calls.
 *
 * Everything else it does arrives over the telemetry socket, so this is deliberately small:
 * sign in, sign up, check who we are, and ask whether there is a demo account to prefill.
 *
 * Results are returned rather than thrown, the same shape `parseServerEvent` uses, because
 * every caller here has something specific to show for each outcome and none of them want a
 * try/catch to tell a wrong password apart from an unplugged network cable.
 */

export interface Profile {
  username: string
  email: string
}

export type AuthFailure =
  /** Credentials were wrong, or the token is no longer accepted. */
  | { reason: 'unauthorized'; message: string }
  /** The backend refused the request and said why. */
  | { reason: 'rejected'; message: string }
  /** Nothing answered, or what answered was not this API. */
  | { reason: 'unreachable'; message: string }

export type AuthResult<T> = { ok: true; value: T } | ({ ok: false } & AuthFailure)

export interface DemoAccount {
  username: string
  password: string
}

const JSON_HEADERS = { 'Content-Type': 'application/json' }

/**
 * The backend answers a refusal with `{ message }`; anything else is shown as a status so a
 * misconfigured address does not surface as a blank error.
 */
async function messageOf(response: Response): Promise<string> {
  try {
    const body: unknown = await response.json()
    if (body && typeof body === 'object') {
      const message = (body as Record<string, unknown>).message
      if (typeof message === 'string' && message) return message
    }
  } catch {
    // Not JSON. The status is all we have.
  }

  return `The proxy answered ${String(response.status)}.`
}

function unreachable(error: unknown): { ok: false } & AuthFailure {
  const detail = error instanceof Error && error.message ? error.message : 'no response'
  return { ok: false, reason: 'unreachable', message: `Could not reach the proxy (${detail}).` }
}

export async function login(
  apiBase: string,
  username: string,
  password: string,
): Promise<AuthResult<string>> {
  let response: Response
  try {
    response = await fetch(`${apiBase}/api/v1/auth/login`, {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify({ username, password }),
    })
  } catch (error) {
    return unreachable(error)
  }

  if (response.status === 401) {
    return {
      ok: false,
      reason: 'unauthorized',
      message: 'That username and password do not match.',
    }
  }
  if (!response.ok) {
    return { ok: false, reason: 'rejected', message: await messageOf(response) }
  }

  let body: unknown
  try {
    body = await response.json()
  } catch (error) {
    return unreachable(error)
  }

  const token = (body as Record<string, unknown> | null)?.token
  if (typeof token !== 'string' || !token) {
    return { ok: false, reason: 'unreachable', message: 'The proxy did not return a token.' }
  }

  return { ok: true, value: token }
}

/**
 * Registration does not hand back a token, so a caller that wants the user signed in has to
 * follow this with `login`.
 */
export async function register(
  apiBase: string,
  username: string,
  email: string,
  password: string,
): Promise<AuthResult<null>> {
  let response: Response
  try {
    response = await fetch(`${apiBase}/api/v1/auth/register`, {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify({ username, email, password }),
    })
  } catch (error) {
    return unreachable(error)
  }

  if (!response.ok) {
    return { ok: false, reason: 'rejected', message: await messageOf(response) }
  }

  return { ok: true, value: null }
}

/**
 * Who the token says we are.
 *
 * Also the app's way of telling a dead session from a dead proxy: a WebSocket that fails to
 * open reports nothing useful about why, but this is an ordinary request and a rejected
 * token comes back as a plain 401.
 */
export async function me(apiBase: string, token: string): Promise<AuthResult<Profile>> {
  let response: Response
  try {
    response = await fetch(`${apiBase}/api/v1/auth/me`, {
      headers: { Authorization: `Bearer ${token}` },
    })
  } catch (error) {
    return unreachable(error)
  }

  if (response.status === 401 || response.status === 403) {
    return { ok: false, reason: 'unauthorized', message: 'The session has ended.' }
  }
  if (!response.ok) {
    return { ok: false, reason: 'rejected', message: await messageOf(response) }
  }

  let body: unknown
  try {
    body = await response.json()
  } catch (error) {
    return unreachable(error)
  }

  const record = (body as Record<string, unknown> | null) ?? {}
  return {
    ok: true,
    value: {
      username: typeof record.username === 'string' ? record.username : '',
      email: typeof record.email === 'string' ? record.email : '',
    },
  }
}

/**
 * The demo credentials, when the backend has been told to publish them.
 *
 * A 404 is the ordinary answer and the only one on a real deployment, so this returns null
 * for every failure rather than an error: there is nothing here for a user to act on.
 */
export async function demoAccount(apiBase: string): Promise<DemoAccount | null> {
  try {
    const response = await fetch(`${apiBase}/api/v1/auth/demo-account`)
    if (!response.ok) return null

    const body: unknown = await response.json()
    const record = (body as Record<string, unknown> | null) ?? {}
    const { username, password } = record

    if (typeof username !== 'string' || typeof password !== 'string') return null
    if (!username || !password) return null

    return { username, password }
  } catch {
    return null
  }
}
