import { useCallback, useState } from 'react'
import { clearSession, loadSession, saveSession, type Session } from './auth/session'
import { Dashboard } from './components/Dashboard'
import { LoginScreen } from './components/LoginScreen'
import { SetupScreen } from './components/SetupScreen'
import { apiBaseOf, loadConfig, saveConfig, type RuntimeConfig } from './config'
import { store } from './engine/store'

/**
 * Three screens: setup, then sign in, then the dashboard. The saved config decides
 * which one opens, and reconfiguring comes back here rather than reloading the page.
 *
 * The demo feed skips the middle one. It reads canned traffic and talks to no backend, so
 * there is nothing for a token to authenticate against — and the pitch has to keep working
 * with no proxy running at all.
 */
export default function App() {
  const [config, setConfig] = useState<RuntimeConfig | null>(() => loadConfig())
  const [editing, setEditing] = useState(false)
  const [session, setSession] = useState<Session | null>(() => {
    const stored = loadConfig()
    return stored ? loadSession(apiBaseOf(stored)) : null
  })

  const signOut = useCallback(() => {
    clearSession()
    setSession(null)
    // Traffic read under the previous session would otherwise sit there looking live.
    store.reset()
  }, [])

  if (!config || editing) {
    return (
      <SetupScreen
        initial={config}
        onCancel={config ? () => setEditing(false) : undefined}
        onSave={(next) => {
          saveConfig(next)
          // Traffic from the previous source would otherwise sit there looking live.
          store.reset()

          // A token is only good for the backend that signed it, so pointing the dashboard
          // somewhere else ends the session rather than carrying it across.
          setSession(loadSession(apiBaseOf(next)))

          setConfig(next)
          setEditing(false)
        }}
      />
    )
  }

  if (config.source === 'ws' && !session) {
    return (
      <LoginScreen
        config={config}
        onSignedIn={(next) => {
          saveSession(next)
          setSession(next)
        }}
        onReconfigure={() => setEditing(true)}
      />
    )
  }

  return (
    <Dashboard
      config={config}
      session={config.source === 'ws' ? session : null}
      onReconfigure={() => setEditing(true)}
      onSignOut={signOut}
      onSessionExpired={signOut}
    />
  )
}
