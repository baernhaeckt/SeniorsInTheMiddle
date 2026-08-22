import { afterEach, describe, expect, it, vi } from 'vitest'
import { demoAccount, login, me, register } from './api'

const API = 'http://proxy:8080'

/** Answers every call with the same response, and records what was asked for. */
function stubFetch(responder: (url: string, init?: RequestInit) => Response | Promise<Response>) {
  const calls: { url: string; init?: RequestInit }[] = []
  vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
    calls.push({ url, init })
    return Promise.resolve(responder(url, init))
  })
  return calls
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('login', () => {
  it('returns the token', async () => {
    const calls = stubFetch(() => json({ token: 'a.b.c' }))

    const result = await login(API, 'ruth', 'secret')

    expect(result).toEqual({ ok: true, value: 'a.b.c' })
    expect(calls[0]?.url).toBe(`${API}/api/v1/auth/login`)
    expect(calls[0]?.init?.body).toBe(JSON.stringify({ username: 'ruth', password: 'secret' }))
  })

  it('reports a wrong password as unauthorized', async () => {
    stubFetch(() => new Response(null, { status: 401 }))

    const result = await login(API, 'ruth', 'wrong')

    expect(result.ok).toBe(false)
    expect(result.ok === false && result.reason).toBe('unauthorized')
  })

  it('passes the backend message through on a 400', async () => {
    stubFetch(() => json({ message: 'Username and password are required' }, 400))

    const result = await login(API, '', '')

    expect(result.ok === false && result.reason).toBe('rejected')
    expect(result.ok === false && result.message).toBe('Username and password are required')
  })

  it('reports an unreachable proxy rather than throwing', async () => {
    stubFetch(() => {
      throw new TypeError('Failed to fetch')
    })

    const result = await login(API, 'ruth', 'secret')

    expect(result.ok === false && result.reason).toBe('unreachable')
  })

  it('treats a 200 with no token as a broken answer', async () => {
    stubFetch(() => json({ nothing: true }))

    const result = await login(API, 'ruth', 'secret')

    expect(result.ok === false && result.reason).toBe('unreachable')
  })
})

describe('register', () => {
  it('succeeds without returning a token', async () => {
    const calls = stubFetch(() => json({ message: 'User registered successfully' }))

    const result = await register(API, 'ruth', 'ruth@test.ch', 'secret')

    expect(result).toEqual({ ok: true, value: null })
    expect(calls[0]?.url).toBe(`${API}/api/v1/auth/register`)
  })

  it('surfaces a duplicate username', async () => {
    stubFetch(() => json({ message: 'Username or email already exists' }, 400))

    const result = await register(API, 'ruth', 'ruth@test.ch', 'secret')

    expect(result.ok === false && result.message).toBe('Username or email already exists')
  })
})

describe('me', () => {
  it('sends the bearer token and returns the profile', async () => {
    const calls = stubFetch(() => json({ username: 'ruth', email: 'ruth@test.ch' }))

    const result = await me(API, 'a.b.c')

    expect(result).toEqual({ ok: true, value: { username: 'ruth', email: 'ruth@test.ch' } })
    expect((calls[0]?.init?.headers as Record<string, string>).Authorization).toBe('Bearer a.b.c')
  })

  it('reports a rejected token as unauthorized', async () => {
    // This is what tells the app a session has ended, since the socket cannot say so.
    stubFetch(() => new Response(null, { status: 401 }))

    const result = await me(API, 'stale')

    expect(result.ok === false && result.reason).toBe('unauthorized')
  })

  it('reports an unreachable proxy separately from a rejected token', async () => {
    stubFetch(() => {
      throw new TypeError('Failed to fetch')
    })

    const result = await me(API, 'a.b.c')

    expect(result.ok === false && result.reason).toBe('unreachable')
  })
})

describe('demoAccount', () => {
  it('returns the advertised credentials', async () => {
    stubFetch(() => json({ username: 'demo', password: 'demo' }))

    expect(await demoAccount(API)).toEqual({ username: 'demo', password: 'demo' })
  })

  it('is null on a 404, which is the normal answer', async () => {
    stubFetch(() => new Response(null, { status: 404 }))

    expect(await demoAccount(API)).toBeNull()
  })

  it('is null when nothing answers', async () => {
    stubFetch(() => {
      throw new TypeError('Failed to fetch')
    })

    expect(await demoAccount(API)).toBeNull()
  })

  it('is null for a half-filled answer', async () => {
    stubFetch(() => json({ username: 'demo' }))

    expect(await demoAccount(API)).toBeNull()
  })
})
