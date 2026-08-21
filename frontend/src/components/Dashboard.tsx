import { useEffect, useState, useSyncExternalStore } from 'react'
import { FlowBand } from './FlowBand'
import { Header } from './Header'
import { Inspector } from './Inspector'
import { SetupGuide } from './SetupGuide'
import { Traffic } from './Traffic'
import { Vault } from './Vault'
import { store } from '../engine/store'
import { createTransport } from '../transport'
import type { RuntimeConfig } from '../config'

interface DashboardProps {
  config: RuntimeConfig
  onReconfigure: () => void
}

export function Dashboard({ config, onReconfigure }: DashboardProps) {
  const state = useSyncExternalStore(store.subscribe, store.getSnapshot)
  const [guideOpen, setGuideOpen] = useState(false)

  useEffect(() => {
    const transport = createTransport(config)
    const offEvent = transport.onEvent((event) => store.apply(event))
    const offStatus = transport.onStatus((status) => store.setLink(status))
    transport.start()

    return () => {
      offEvent()
      offStatus()
      transport.stop()
    }
  }, [config])

  return (
    <div className="shell">
      <Header
        state={state}
        config={config}
        onOpenGuide={() => setGuideOpen(true)}
        onReconfigure={onReconfigure}
      />
      <FlowBand state={state} />
      <div className="floor">
        <Traffic state={state} />
        <Inspector state={state} />
        <Vault state={state} />
      </div>

      {guideOpen && <SetupGuide config={config} onClose={() => setGuideOpen(false)} />}
    </div>
  )
}
