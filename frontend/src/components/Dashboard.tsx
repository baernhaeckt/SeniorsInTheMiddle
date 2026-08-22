import { useEffect, useState } from 'react'
import type { RuntimeConfig } from '../config'
import { store } from '../engine/store'
import { createTransport } from '../transport'
import { FlowBand } from './FlowBand'
import { Header } from './Header'
import { Inspector } from './Inspector'
import { SetupGuide } from './SetupGuide'
import { Traffic } from './Traffic'
import { Vault } from './Vault'

interface DashboardProps {
  config: RuntimeConfig
  onReconfigure: () => void
}

/**
 * Wires the chosen transport into the store for as long as this config is in
 * use. Each panel subscribes to the slice of the store it draws, so an event
 * only re-renders the panels it touches.
 */
export function Dashboard({ config, onReconfigure }: DashboardProps) {
  const [guideOpen, setGuideOpen] = useState(false)

  useEffect(() => {
    const transport = createTransport(config)
    const offEvent = transport.onEvent((event) => {
      store.apply(event)
    })
    const offStatus = transport.onStatus((status) => {
      store.setLink(status)
    })
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
        config={config}
        onOpenGuide={() => setGuideOpen(true)}
        onReconfigure={onReconfigure}
      />
      <FlowBand />
      <div className="floor">
        <Traffic />
        <Inspector />
        <Vault />
      </div>

      {guideOpen && <SetupGuide config={config} onClose={() => setGuideOpen(false)} />}
    </div>
  )
}
