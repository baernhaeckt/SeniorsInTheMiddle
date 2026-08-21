import { useEffect, useState, useSyncExternalStore } from 'react'
import { FlowBand } from './components/FlowBand'
import { Header } from './components/Header'
import { Inspector } from './components/Inspector'
import { SetupGuide } from './components/SetupGuide'
import { Traffic } from './components/Traffic'
import { Vault } from './components/Vault'
import { store } from './engine/store'
import { createTransport } from './transport'

export default function App() {
  const state = useSyncExternalStore(store.subscribe, store.getSnapshot)
  const [setupOpen, setSetupOpen] = useState(false)

  useEffect(() => {
    const transport = createTransport()
    const offEvent = transport.onEvent((event) => store.apply(event))
    const offStatus = transport.onStatus((status) => store.setLink(status))
    transport.start()

    return () => {
      offEvent()
      offStatus()
      transport.stop()
    }
  }, [])

  return (
    <div className="shell">
      <Header state={state} onOpenSetup={() => setSetupOpen(true)} />
      <FlowBand state={state} />
      <div className="floor">
        <Traffic state={state} />
        <Inspector state={state} />
        <Vault state={state} />
      </div>

      {setupOpen && <SetupGuide onClose={() => setSetupOpen(false)} />}
    </div>
  )
}
