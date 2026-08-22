import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { BLANK_CONFIG, type RuntimeConfig } from '../config'
import { LoginScreen } from './LoginScreen'

const CONFIG: RuntimeConfig = { ...BLANK_CONFIG, hubUrl: 'http://proxy:8080/hub/telemetry' }
const API = 'http://proxy:8080'
const TOKEN = 'a.b.c'

interface Route {
  status?: number
  body?: unknown
}

/**
 * Answers each auth endpoint from a small table, so a test only names the calls it cares
 * about. Anything unlisted 404s, which is what a backend without a demo account does.
 */
function stubApi(routes: Record<string, Route>) {
  const calls: string[] = []

  vi.stubGlobal('fetch', (url: string) => {
    const path = url.replace(API, '')
    calls.push(path)

    const route = routes[path]
    if (!route) return Promise.resolve(new Response(null, { status: 404 }))

    return Promise.resolve(
      new Response(route.body === undefined ? null : JSON.stringify(route.body), {
        status: route.status ?? 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
  })

  return calls
}

function renderScreen(routes: Record<string, Route> = {}) {
  const calls = stubApi(routes)
  const onSignedIn = vi.fn()
  const onReconfigure = vi.fn()

  render(<LoginScreen config={CONFIG} onSignedIn={onSignedIn} onReconfigure={onReconfigure} />)

  return { calls, onSignedIn, onReconfigure }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('LoginScreen', () => {
  it('signs in and hands back a session, with the email taken from the profile', async () => {
    // Nobody types an email to sign in, so the only way the bubble can show one is to ask.
    const { onSignedIn } = renderScreen({
      '/api/v1/auth/login': { body: { token: TOKEN } },
      '/api/v1/auth/me': { body: { username: 'ruth', email: 'ruth@test.ch' } },
    })

    await userEvent.type(screen.getByLabelText('Username'), 'ruth')
    await userEvent.type(screen.getByLabelText('Password'), 'secret')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    await waitFor(() => {
      expect(onSignedIn).toHaveBeenCalledWith({
        token: TOKEN,
        username: 'ruth',
        email: 'ruth@test.ch',
        origin: API,
      })
    })
  })

  it('still signs in when the profile lookup fails', async () => {
    // A missing email is not a reason to refuse a session that otherwise works.
    const { onSignedIn } = renderScreen({ '/api/v1/auth/login': { body: { token: TOKEN } } })

    await userEvent.type(screen.getByLabelText('Username'), 'ruth')
    await userEvent.type(screen.getByLabelText('Password'), 'secret')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    await waitFor(() => {
      expect(onSignedIn).toHaveBeenCalledWith({
        token: TOKEN,
        username: 'ruth',
        email: '',
        origin: API,
      })
    })
  })

  it('shows the reason a sign-in was refused', async () => {
    renderScreen({ '/api/v1/auth/login': { status: 401 } })

    await userEvent.type(screen.getByLabelText('Username'), 'ruth')
    await userEvent.type(screen.getByLabelText('Password'), 'wrong')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('do not match')
  })

  it('will not submit an empty form', async () => {
    const { calls } = renderScreen()

    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('both needed')
    expect(calls).not.toContain('/api/v1/auth/login')
  })

  it('asks for an email only when creating an account', async () => {
    renderScreen()

    expect(screen.queryByLabelText('Email')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('radio', { name: 'Create account' }))

    expect(screen.getByLabelText('Email')).toBeInTheDocument()
  })

  it('signs in straight after registering, without asking again', async () => {
    // Registration returns no token, so the screen has to follow it with a login itself.
    const { calls, onSignedIn } = renderScreen({
      '/api/v1/auth/register': { body: { message: 'User registered successfully' } },
      '/api/v1/auth/login': { body: { token: TOKEN } },
      '/api/v1/auth/me': { body: { username: 'ruth', email: 'ruth@test.ch' } },
    })

    await userEvent.click(screen.getByRole('radio', { name: 'Create account' }))
    await userEvent.type(screen.getByLabelText('Username'), 'ruth')
    await userEvent.type(screen.getByLabelText('Email'), 'ruth@test.ch')
    await userEvent.type(screen.getByLabelText('Password'), 'secret')
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }))

    await waitFor(() => {
      expect(onSignedIn).toHaveBeenCalledWith({
        token: TOKEN,
        username: 'ruth',
        email: 'ruth@test.ch',
        origin: API,
      })
    })
    expect(calls).toContain('/api/v1/auth/register')
    expect(calls).toContain('/api/v1/auth/login')
  })

  it('does not sign in when registration was refused', async () => {
    const { calls } = renderScreen({
      '/api/v1/auth/register': {
        status: 400,
        body: { message: 'Username or email already exists' },
      },
    })

    await userEvent.click(screen.getByRole('radio', { name: 'Create account' }))
    await userEvent.type(screen.getByLabelText('Username'), 'ruth')
    await userEvent.type(screen.getByLabelText('Email'), 'ruth@test.ch')
    await userEvent.type(screen.getByLabelText('Password'), 'secret')
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('already exists')
    expect(calls).not.toContain('/api/v1/auth/login')
  })

  describe('demo prefill', () => {
    it('fills the fields and says why', async () => {
      renderScreen({
        '/api/v1/auth/demo-account': { body: { username: 'demo', password: 'demo' } },
      })

      await waitFor(() => {
        expect(screen.getByLabelText('Username')).toHaveValue('demo')
      })
      expect(screen.getByLabelText('Password')).toHaveValue('demo')
      expect(screen.getByText(/prefilled/i)).toBeInTheDocument()
    })

    it('leaves the form alone when there is no demo account', async () => {
      // The 404 case, which is every real deployment.
      renderScreen()

      await waitFor(() => {
        expect(screen.getByLabelText('Username')).toHaveValue('')
      })
      expect(screen.queryByText(/prefilled/i)).not.toBeInTheDocument()
    })

    it('never overwrites something already typed', async () => {
      let release: (() => void) | undefined
      const held = new Promise<void>((resolve) => {
        release = resolve
      })

      vi.stubGlobal('fetch', async (url: string) => {
        if (url.endsWith('/demo-account')) {
          await held
          return new Response(JSON.stringify({ username: 'demo', password: 'demo' }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        }
        return new Response(null, { status: 404 })
      })

      render(<LoginScreen config={CONFIG} onSignedIn={vi.fn()} onReconfigure={vi.fn()} />)

      await userEvent.type(screen.getByLabelText('Username'), 'ruth')
      release?.()

      // Give the late response every chance to land before asserting it did not.
      await waitFor(() => {
        expect(screen.getByLabelText('Username')).toHaveValue('ruth')
      })
      expect(screen.queryByText(/prefilled/i)).not.toBeInTheDocument()
    })

    it('drops the note once the viewer edits the form', async () => {
      renderScreen({
        '/api/v1/auth/demo-account': { body: { username: 'demo', password: 'demo' } },
      })

      await waitFor(() => {
        expect(screen.getByText(/prefilled/i)).toBeInTheDocument()
      })

      await userEvent.type(screen.getByLabelText('Username'), 'x')

      expect(screen.queryByText(/prefilled/i)).not.toBeInTheDocument()
    })
  })

  describe('an address that does not answer', () => {
    it('names the address and this origin, since a fetch cannot tell them apart', async () => {
      // A blocked origin and a dead socket both reject with the same opaque TypeError, so
      // the note has to cover both — and the origin is the string someone needs when it
      // turns out to be the allow-list.
      vi.stubGlobal('fetch', () => Promise.reject(new TypeError('Failed to fetch')))
      render(<LoginScreen config={CONFIG} onSignedIn={vi.fn()} onReconfigure={vi.fn()} />)

      const note = await screen.findByRole('status')

      expect(note).toHaveTextContent('No answer from')
      expect(note).toHaveTextContent(API)
      expect(note).toHaveTextContent(window.location.origin)
    })

    it('stays quiet when the address answers, demo account or not', async () => {
      renderScreen()

      await waitFor(() => {
        expect(screen.getByLabelText('Username')).toHaveValue('')
      })
      expect(screen.queryByRole('status')).not.toBeInTheDocument()
    })
  })

  it('offers a way back to the setup screen', async () => {
    const { onReconfigure } = renderScreen()

    await userEvent.click(screen.getByRole('button', { name: 'Change' }))

    expect(onReconfigure).toHaveBeenCalled()
  })
})
