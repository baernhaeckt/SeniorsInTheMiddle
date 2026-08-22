import { describe, expect, it } from 'vitest'
import {
  clearSession,
  expiryOf,
  initialsOf,
  isExpired,
  loadSession,
  saveSession,
  SESSION_KEY,
  type Session,
} from './session'

const ORIGIN = 'http://proxy:8080'

/** A JWT with the given expiry. Only the payload is ever read, so the rest is filler. */
function tokenExpiring(atMs: number): string {
  const payload = btoa(JSON.stringify({ exp: Math.floor(atMs / 1000), name: 'ruth' }))
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
  return `header.${payload}.signature`
}

function storage(initial: Record<string, string> = {}) {
  const values = new Map(Object.entries(initial))
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => {
      values.set(key, value)
    },
    removeItem: (key: string) => {
      values.delete(key)
    },
    values,
  }
}

const FAR_FUTURE = Date.now() + 48 * 60 * 60 * 1000

function sessionFor(token: string, origin = ORIGIN): Session {
  return { token, username: 'ruth', email: 'ruth@test.ch', origin }
}

describe('expiryOf', () => {
  it('reads the exp claim as milliseconds', () => {
    const at = 1_800_000_000_000
    expect(expiryOf(tokenExpiring(at))).toBe(at)
  })

  it('returns null for something that is not a token', () => {
    expect(expiryOf('nonsense')).toBeNull()
    expect(expiryOf('')).toBeNull()
    expect(expiryOf('a.b.c')).toBeNull()
  })

  it('returns null when there is no exp claim', () => {
    const payload = btoa(JSON.stringify({ name: 'ruth' }))
    expect(expiryOf(`h.${payload}.s`)).toBeNull()
  })
})

describe('isExpired', () => {
  it('is false well before the expiry', () => {
    expect(isExpired(tokenExpiring(FAR_FUTURE))).toBe(false)
  })

  it('is true after the expiry', () => {
    expect(isExpired(tokenExpiring(Date.now() - 1000))).toBe(true)
  })

  it('treats a token about to lapse as already gone', () => {
    // Opening a socket with a token that expires mid-handshake just fails less clearly.
    expect(isExpired(tokenExpiring(Date.now() + 1000))).toBe(true)
  })

  it('keeps a token whose expiry cannot be read', () => {
    // The server is the one that decides; a parsing quirk should not sign anyone out.
    expect(isExpired('not-a-jwt')).toBe(false)
  })
})

describe('loadSession', () => {
  it('round-trips a saved session', () => {
    const store = storage()
    const session = sessionFor(tokenExpiring(FAR_FUTURE))

    saveSession(session, store)

    expect(loadSession(ORIGIN, store)).toEqual(session)
  })

  it('is null when nothing is stored', () => {
    expect(loadSession(ORIGIN, storage())).toBeNull()
  })

  it('is null for a corrupt value', () => {
    expect(loadSession(ORIGIN, storage({ [SESSION_KEY]: 'not json' }))).toBeNull()
  })

  it('is null when a field is missing', () => {
    const store = storage({ [SESSION_KEY]: JSON.stringify({ token: 'x' }) })
    expect(loadSession(ORIGIN, store)).toBeNull()
  })

  it('is null once the token has expired', () => {
    const store = storage()
    saveSession(sessionFor(tokenExpiring(Date.now() - 1000)), store)

    expect(loadSession(ORIGIN, store)).toBeNull()
  })

  it('is null when the dashboard now points at a different backend', () => {
    // A token signed by one proxy means nothing to another, so it must not follow the
    // dashboard across a reconfigure.
    const store = storage()
    saveSession(sessionFor(tokenExpiring(FAR_FUTURE)), store)

    expect(loadSession('http://elsewhere:8080', store)).toBeNull()
  })

  it('survives storage being switched off', () => {
    const throwing = {
      getItem: () => {
        throw new Error('denied')
      },
    }
    expect(loadSession(ORIGIN, throwing)).toBeNull()
  })
})

describe('clearSession', () => {
  it('removes the stored session', () => {
    const store = storage()
    saveSession(sessionFor(tokenExpiring(FAR_FUTURE)), store)

    clearSession(store)

    expect(loadSession(ORIGIN, store)).toBeNull()
  })
})

describe('initialsOf', () => {
  it('takes two letters from a single word', () => {
    expect(initialsOf('ruth')).toBe('RU')
  })

  it('takes one from each of two words', () => {
    expect(initialsOf('ruth meier')).toBe('RM')
    expect(initialsOf('ruth.meier')).toBe('RM')
    expect(initialsOf('ruth_meier')).toBe('RM')
  })

  it('falls back for an empty name', () => {
    expect(initialsOf('   ')).toBe('?')
  })
})
