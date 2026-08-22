import { useEffect, useRef, useState } from 'react'
import { me } from '../auth/api'
import type { Session } from '../auth/session'
import { apiBaseOf, type RuntimeConfig } from '../config'
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
  /** Null on the demo feed, which authenticates against nothing. */
  session: Session | null
  onReconfigure: () => void
  onSignOut: () => void
  /** The proxy rejected the session; the app should send the viewer back to the login screen. */
  onSessionExpired: () => void
}

/**
 * Wires the chosen transport into the store for as long as this config is in
 * use. Each panel subscribes to the slice of the store it draws, so an event
 * only re-renders the panels it touches.
 */
export function Dashboard({
  config,
  session,
  onReconfigure,
  onSignOut,
  onSessionExpired,
}: DashboardProps) {
  const [guideOpen, setGuideOpen] = useState(false)

  // Read inside the connection effect rather than captured by it, so a re-render with a new
  // token does not tear down a working connection just to hand it the same socket back.
  const tokenRef = useRef<string | null>(session?.token ?? null)
  const expiredRef = useRef(onSessionExpired)

  // Declared before the connection effect so both are current by the time it first runs.
  useEffect(() => {
    tokenRef.current = session?.token ?? null
    expiredRef.current = onSessionExpired
  })

  useEffect(() => {
    let done = false
    // One check per outage, not one per retry: the backoff loop would otherwise fire a
    // request every few seconds at a proxy that is already known to be down.
    let checking = false

    const transport = createTransport(config, {
      getToken: () => tokenRef.current,
      onConnectFailed: () => {
        const token = tokenRef.current
        if (done || checking || !token) return
        checking = true

        // A failed WebSocket upgrade does not report its status code, so the only way to
        // tell "the token is no longer accepted" from "nothing is listening" is to ask
        // something that answers in HTTP.
        void me(apiBaseOf(config), token).then((result) => {
          checking = false
          if (done) return
          if (!result.ok && result.reason === 'unauthorized') expiredRef.current()
        })
      },
    })

    const offEvent = transport.onEvent((event) => {
      store.apply(event)
    })
    const offStatus = transport.onStatus((status) => {
      store.setLink(status)
    })
    transport.start()

    return () => {
      done = true
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
        session={session}
        onSignOut={onSignOut}
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
