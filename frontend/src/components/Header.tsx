import { Fragment, type ReactNode } from 'react'
import type { Session } from '../auth/session'
import { hasProxyAddress, proxyAddressOf, type RuntimeConfig } from '../config'
import { COPY } from '../copy'
import { median } from '../engine/store'
import { useStore } from '../engine/useStore'
import { useProjector } from '../ui/projector'
import { PROTOCOL_VERSION } from '../protocol/types'
import type { LinkState } from '../transport/types'
import { Mark, Wordmark } from './Brand'
import { Clip } from './Clip'
import { Peek } from './Peek'
import { Sparkline } from './Sparkline'
import { UserBubble } from './UserBubble'
import type { ProxyPolicy, ServiceState } from '../protocol/types'

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
  const [projector, toggleProjector] = useProjector()
  const metrics = useStore((state) => state.metrics)
  const link = useStore((state) => state.link)
  const protocolVersion = useStore((state) => state.protocolVersion)
  const policy = useStore((state) => state.policy)
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
          {metrics.blocks > 0 && <Stat label="Blocks" value={metrics.blocks} variant="blocks" />}
          <Stat
            label="Round trip"
            value={p50 ?? '—'}
            unit={p50 === null ? '' : 'ms'}
            variant="trip"
            lead={<Sparkline values={metrics.latencies} />}
          />
        </div>

        <Peek
          className="head__link"
          value={link.endpoint}
          note={linkNote}
          align="end"
          extra={policy ? <PolicySheet policy={policy} /> : undefined}
        >
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
            {policy && (
              <span className="services" aria-label="Detector services">
                <Service name="pii" state={policy.services.pii} />
                <Service name="privacy" state={policy.services.privacyCheck} />
              </span>
            )}
          </div>
        </Peek>

        <button type="button" className="reconf" onClick={onReconfigure}>
          Reconfigure
        </button>

        {/*
          Last, because projector mode hides Reconfigure: anything ahead of that
          button moves when the mode is switched, and this is the button you go
          back to. Here its distance from the right edge is the same either way.
        */}
        <button
          type="button"
          className="tsize"
          data-on={projector}
          aria-pressed={projector}
          aria-label="Projector mode"
          title="Larger type, less detail. For a projector or a wall display."
          onClick={toggleProjector}
        >
          <span className="tsize__mark" aria-hidden="true">
            A<b>A</b>
          </span>
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
  variant?: 'held' | 'treated' | 'blocks' | 'trip'
  /** Something drawn before the number, a sparkline say. */
  lead?: ReactNode
}

function Stat({ label, value, unit, variant, lead }: StatProps) {
  return (
    <div className={`stat${variant ? ` stat--${variant}` : ''}`}>
      <div className="stat__value">
        {lead}
        <span>
          {value}
          {unit ? <small>{unit}</small> : null}
        </span>
      </div>
      <div className="u-label stat__label">{label}</div>
    </div>
  )
}

const SERVICE_COPY: Record<ServiceState, string> = {
  ok: 'answering',
  disabled: 'not configured',
  down: 'not answering',
}

function Service({ name, state }: { name: string; state: ServiceState }) {
  return (
    <span className="service" data-state={state} title={`${name} service ${SERVICE_COPY[state]}`}>
      <span className="service__dot" />
      {name}
    </span>
  )
}

/** What the proxy said it does, under the link address in the popover. */
function PolicySheet({ policy }: { policy: ProxyPolicy }) {
  const scoped = Object.entries(policy.inspectOnly)
  return (
    <dl className="policy">
      <dt>bodies</dt>
      <dd>{policy.rewrite ? 'rewritten before they leave' : 'observed, never changed'}</dd>
      <dt>threshold</dt>
      <dd>{Math.round(policy.confidenceThreshold * 100)}% confidence</dd>
      <dt>max body</dt>
      <dd>{Math.round(policy.maxBodyBytes / 1024)} kB</dd>
      <dt>bypassed</dt>
      <dd>{policy.bypassHosts.length === 0 ? 'nothing' : policy.bypassHosts.join(', ')}</dd>
      {scoped.map(([host, paths]) => (
        <Fragment key={host}>
          <dt>{host}</dt>
          <dd>only {paths.join(', ')}</dd>
        </Fragment>
      ))}
    </dl>
  )
}
