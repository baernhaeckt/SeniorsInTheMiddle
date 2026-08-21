import { useState } from 'react'
import { Dashboard } from './components/Dashboard'
import { SetupScreen } from './components/SetupScreen'
import { loadConfig, saveConfig, type RuntimeConfig } from './config'
import { store } from './engine/store'

/**
 * Two screens: setup, then the dashboard. The saved config decides which one
 * opens, and reconfiguring comes back here rather than reloading the page.
 */
export default function App() {
  const [config, setConfig] = useState<RuntimeConfig | null>(() => loadConfig())
  const [editing, setEditing] = useState(false)

  if (!config || editing) {
    return (
      <SetupScreen
        initial={config}
        onCancel={config ? () => setEditing(false) : undefined}
        onSave={(next) => {
          saveConfig(next)
          // Traffic from the previous source would otherwise sit there looking live.
          store.reset()
          setConfig(next)
          setEditing(false)
        }}
      />
    )
  }

  return <Dashboard config={config} onReconfigure={() => setEditing(true)} />
}
