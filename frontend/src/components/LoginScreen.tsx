import { useEffect, useRef, useState, type FormEvent } from 'react'
import { login, me, probeAuth, register } from '../auth/api'
import type { Session } from '../auth/session'
import { apiBaseOf, type RuntimeConfig } from '../config'
import { COPY } from '../copy'
import { Mark, Wordmark } from './Brand'

type Mode = 'signin' | 'register'

interface LoginScreenProps {
  config: RuntimeConfig
  onSignedIn: (session: Session) => void
  /** Back to the setup screen, for when the address itself is the problem. */
  onReconfigure: () => void
}

/**
 * The gate in front of the dashboard.
 *
 * The stream on the other side carries decrypted request bodies out of someone's household,
 * so this is the one screen the app will not skip. Signing in and signing up are the same
 * card with one field's difference between them, because a household setting the proxy up for
 * the first time does both within a minute of each other.
 */
export function LoginScreen({ config, onSignedIn, onReconfigure }: LoginScreenProps) {
  const apiBase = apiBaseOf(config)

  const [mode, setMode] = useState<Mode>('signin')
  const [username, setUsername] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState(false)
  const [prefilled, setPrefilled] = useState(false)
  /** Null until the address has been tried, so nothing is claimed before it is known. */
  const [reached, setReached] = useState<boolean | null>(null)

  // A late demo response must not overwrite something already being typed.
  const touched = useRef(false)
  const markTouched = () => {
    touched.current = true
    setPrefilled(false)
  }

  useEffect(() => {
    let cancelled = false

    void probeAuth(apiBase).then((probe) => {
      if (cancelled) return
      setReached(probe.reached)
      if (!probe.demo || touched.current) return
      setUsername(probe.demo.username)
      setPassword(probe.demo.password)
      setPrefilled(true)
    })

    return () => {
      cancelled = true
    }
  }, [apiBase])

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (pending) return

    const name = username.trim()
    if (!name || !password) {
      setError('Username and password are both needed.')
      return
    }
    if (mode === 'register' && !email.trim()) {
      setError('An email address is needed to create an account.')
      return
    }

    setPending(true)
    setError(null)

    if (mode === 'register') {
      const created = await register(apiBase, name, email.trim(), password)
      if (!created.ok) {
        setError(created.message)
        setPending(false)
        return
      }
      // Registration returns no token, so the account is used immediately to get one and
      // nobody is asked for the password they just chose a second time.
    }

    const signedIn = await login(apiBase, name, password)
    if (!signedIn.ok) {
      setError(signedIn.message)
      setPending(false)
      return
    }

    // Someone signing in never typed an email, and the username they typed may differ in
    // case from the stored one. The token knows both, so the profile is what gets kept —
    // falling back to what was typed if that call does not make it, since a session that
    // works is worth more than a complete-looking one.
    const profile = await me(apiBase, signedIn.value)

    onSignedIn({
      token: signedIn.value,
      username: (profile.ok && profile.value.username) || name,
      email: profile.ok ? profile.value.email : email.trim(),
      origin: apiBase,
    })
  }

  const switchTo = (next: Mode) => {
    setMode(next)
    setError(null)
  }

  return (
    <div className="signin">
      <div className="signin__card">
        <aside className="signin__art" aria-hidden="true">
          <Mark className="signin__mark" />

          <div className="signin__rail">
            {MOTES.map((mote) => (
              <span
                key={mote.delay}
                className="signin__mote"
                data-kind={mote.kind}
                style={{ animationDelay: `${String(mote.delay)}ms`, top: `${String(mote.top)}%` }}
              />
            ))}
            <span className="signin__wall" />
          </div>

          <p className="signin__thesis">
            Everything that identifies a household is held back at the boundary. The rest goes on
            untouched.
          </p>
        </aside>

        <form className="signin__form" onSubmit={(event) => void submit(event)} noValidate>
          <header className="signin__head">
            <h1 className="signin__name u-display">
              <Wordmark />
            </h1>
            <p className="u-label signin__sub">{COPY.tagline}</p>
          </header>

          <div className="signin__modes" role="group" aria-label="Sign in or create an account">
            <ModeChoice
              value="signin"
              current={mode}
              label="Sign in"
              onPick={() => {
                switchTo('signin')
              }}
            />
            <ModeChoice
              value="register"
              current={mode}
              label="Create account"
              onPick={() => {
                switchTo('register')
              }}
            />
          </div>

          <Field
            id="username"
            label="Username"
            value={username}
            autoComplete="username"
            onChange={(value) => {
              markTouched()
              setUsername(value)
            }}
          />

          {mode === 'register' && (
            <Field
              id="email"
              label="Email"
              type="email"
              value={email}
              autoComplete="email"
              onChange={(value) => {
                markTouched()
                setEmail(value)
              }}
            />
          )}

          <Field
            id="password"
            label="Password"
            type="password"
            value={password}
            autoComplete={mode === 'register' ? 'new-password' : 'current-password'}
            onChange={(value) => {
              markTouched()
              setPassword(value)
            }}
          />

          {prefilled && mode === 'signin' && (
            <p className="signin__demo u-label">Demo account · prefilled, press sign in</p>
          )}

          {reached === false && (
            <p className="signin__offline" role="status">
              No answer from <span className="u-mono">{apiBase || 'no address set'}</span>. Either
              nothing is listening there, or this origin —{' '}
              <span className="u-mono">{window.location.origin}</span> — is not one the proxy
              allows.
            </p>
          )}

          {error && (
            <p className="signin__error" role="alert">
              {error}
            </p>
          )}

          <button type="submit" className="btn btn--go signin__go" disabled={pending}>
            {pending ? 'Working…' : mode === 'register' ? 'Create account' : 'Sign in'}
          </button>

          <p className="signin__where" data-offline={reached === false}>
            <span className="u-mono">{apiBase || 'no address set'}</span>
            <button type="button" className="group__more" onClick={onReconfigure}>
              Change
            </button>
          </p>
        </form>
      </div>
    </div>
  )
}

/**
 * The drifting packets on the art panel: warm ones are held at the wall, cool ones pass. Pure
 * decoration, but it is the same story the dashboard's band tells, so the login screen is not
 * the one place the product says nothing about itself.
 */
const MOTES = [
  { delay: 0, top: 18, kind: 'cool' },
  { delay: 900, top: 34, kind: 'warm' },
  { delay: 1700, top: 52, kind: 'cool' },
  { delay: 2600, top: 68, kind: 'warm' },
  { delay: 3400, top: 84, kind: 'cool' },
  { delay: 4200, top: 26, kind: 'cool' },
] as const

interface ModeChoiceProps {
  value: Mode
  current: Mode
  label: string
  onPick: () => void
}

function ModeChoice({ value, current, label, onPick }: ModeChoiceProps) {
  const picked = current === value
  return (
    <label className="signin__mode" data-picked={picked}>
      <input
        type="radio"
        name="mode"
        value={value}
        checked={picked}
        onChange={onPick}
        className="choice__radio"
      />
      <span>{label}</span>
    </label>
  )
}

interface FieldProps {
  id: string
  label: string
  value: string
  type?: string
  autoComplete?: string
  onChange: (value: string) => void
}

function Field({ id, label, value, type = 'text', autoComplete, onChange }: FieldProps) {
  return (
    <div className="input">
      <label className="u-label input__label" htmlFor={id}>
        {label}
      </label>
      <input
        id={id}
        className="input__box"
        type={type}
        value={value}
        autoComplete={autoComplete}
        spellCheck={false}
        onChange={(event) => {
          onChange(event.target.value)
        }}
      />
    </div>
  )
}
