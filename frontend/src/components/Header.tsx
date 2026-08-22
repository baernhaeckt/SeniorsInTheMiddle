import type { Session } from '../auth/session'
import { hasProxyAddress, proxyAddressOf, type RuntimeConfig } from '../config'
import { COPY } from '../copy'
import { median } from '../engine/store'
import { useStore } from '../engine/useStore'
import { PROTOCOL_VERSION } from '../protocol/types'
import type { LinkState } from '../transport/types'
import { Mark, Wordmark } from './Brand'
import { Clip } from './Clip'
import { Peek } from './Peek'
import { UserBubble } from './UserBubble'

const LINK_COPY: Record<LinkState, string> = {
  idle: 'not attached',
  connecting: 'attaching',
  live: 'live',
  retrying: 'reattaching',
  closed: 'detached',
}

interface HeaderProps {
  config: RuntimeConfig
  onOpenGuide: () => void
  onReconfigure: () => void
  /** Absent on the demo feed, which signs nobody in. */
  session: Session | null
  onSignOut: () => void
}

export function Header({ config, onOpenGuide, onReconfigure, session, onSignOut }: HeaderProps) {
  const metrics = useStore((state) => state.metrics)
  const link = useStore((state) => state.link)
  const protocolVersion = useStore((state) => state.protocolVersion)
  const p50 = median(metrics.latencies)
  const configured = hasProxyAddress(config)
  const mismatch = protocolVersion !== null && protocolVersion !== PROTOCOL_VERSION

  const address = configured ? proxyAddressOf(config) : ''
  const linkNote = [
    link.detail,
    link.dropped ? `${link.dropped} frame${link.dropped === 1 ? '' : 's'} dropped` : '',
  ]
    .filter(Boolean)
    .join(' · ')

  return (
    <header className="head">
      <div className="head__id">
        <Mark className="head__mark" />
        <div>
          <h1 className="head__name u-display">
            <Wordmark />
          </h1>
          <div className="u-label head__sub">{COPY.tagline}</div>
        </div>
      </div>

      <Peek className="head__cta" value={address}>
        <button type="button" className="setupcta" data-unset={!configured} onClick={onOpenGuide}>
          <span className="setupcta__label">Proxy address</span>
          {configured ? (
            <Clip className="setupcta__addr" value={address} />
          ) : (
            <span className="setupcta__addr">not set</span>
          )}
          <span className="setupcta__go" aria-hidden="true">
            &rarr;
          </span>
        </button>
      </Peek>

      <div className="head__right">
        <div className="stats">
          <Stat label="Requests" value={metrics.requests} />
          <Stat label="Treated" value={metrics.treated} variant="treated" />
          <Stat label="Identifiers held" value={metrics.identifiersHeld} variant="held" />
          <Stat label="Round trip" value={p50 ?? '—'} unit={p50 === null ? '' : 'ms'} />
        </div>

        <Peek className="head__link" value={link.endpoint} note={linkNote} align="end">
          <div
            className="link"
            data-state={link.state}
            data-warn={mismatch || Boolean(link.dropped)}
          >
            <span className="link__dot" />
            <span className="link__text">
              <span className="link__state">{LINK_COPY[link.state]}&nbsp;·&nbsp;</span>
              <Clip value={link.endpoint} />
              {mismatch && (
                <span className="link__warn">
                  &nbsp;· protocol v{protocolVersion}, expected v{PROTOCOL_VERSION}
                </span>
              )}
              {link.dropped ? (
                <span className="link__warn">&nbsp;· {link.dropped} dropped</span>
              ) : null}
            </span>
          </div>
        </Peek>

        <button type="button" className="reconf" onClick={onReconfigure}>
          Reconfigure
        </button>

        {session && <UserBubble session={session} onSignOut={onSignOut} />}
      </div>
    </header>
  )
}

interface StatProps {
  label: string
  value: number | string
  unit?: string
  variant?: 'held' | 'treated'
}

function Stat({ label, value, unit, variant }: StatProps) {
  return (
    <div className={`stat${variant ? ` stat--${variant}` : ''}`}>
      <div className="stat__value">
        {value}
        {unit ? <small>{unit}</small> : null}
      </div>
      <div className="u-label stat__label">{label}</div>
    </div>
  )
}
