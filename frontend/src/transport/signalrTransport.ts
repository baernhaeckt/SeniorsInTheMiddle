import { HttpTransportType, HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { parseServerEvent, type ServerEvent } from '../protocol/types'
import { createEmitter, type LinkStatus, type Transport } from './types'

export const BACKOFF_MS = [500, 1000, 2000, 4000, 8000, 15000] as const
/** Spread reconnects out, so several wall displays do not hammer the proxy in step. */
const JITTER = 0.2

/**
 * The part of a SignalR connection this transport uses. Narrow on purpose: a fake in a
 * test satisfies it without pulling the real client into jsdom, which has neither a
 * WebSocket nor a fetch to give it.
 */
export interface HubLike {
  start(): Promise<void>
  stop(): Promise<void>
  on(method: string, handler: (payload: never) => void): void
  onclose(handler: (error?: Error) => void): void
}

export interface SignalRTransportOptions {
  connectionFactory?: (url: string, accessTokenFactory?: () => string) => HubLike
  random?: () => number
  /** Called once per rejected frame, with the reason. Defaults to console.warn, throttled per reason. */
  onRejected?: (detail: string) => void
  /**
   * The token to authenticate the handshake with, read fresh on every connect attempt so a
   * sign-out or a new sign-in is picked up by the retry loop without rebuilding the transport.
   */
  getToken?: () => string | null
  /**
   * Called when a connection attempt fails, before the retry is scheduled.
   *
   * A browser cannot see the status code behind a failed WebSocket upgrade, so this transport
   * genuinely cannot tell a rejected token from an unreachable proxy. It reports the failure
   * and lets the app decide — the app can ask the REST API, which does answer that question.
   */
  onConnectFailed?: (detail: string) => void
}

export function backoffFor(attempt: number, random = Math.random): number {
  const base = BACKOFF_MS[Math.min(attempt, BACKOFF_MS.length - 1)] ?? 0
  return Math.round(base * (1 + (random() * 2 - 1) * JITTER))
}

/**
 * Negotiation is skipped and the transport pinned to WebSockets, so the browser opens the
 * socket directly instead of posting to /negotiate first. That keeps the page's
 * connect-src policy to ws:/wss: and takes CORS out of the picture entirely — which
 * matters, because the proxy's address is typed in at runtime and cannot be baked into a
 * Content-Security-Policy at build time.
 *
 * The same choice is why the token travels as `accessTokenFactory` rather than a header:
 * there is no negotiate request to put an Authorization header on, and the browser's
 * WebSocket API cannot set one either. Given this factory, the SignalR client appends the
 * token to the socket URL as `?access_token=`, which is what the hub reads it from.
 */
function buildConnection(url: string, accessTokenFactory?: () => string): HubLike {
  return new HubConnectionBuilder()
    .withUrl(url, {
      skipNegotiation: true,
      transport: HttpTransportType.WebSockets,
      accessTokenFactory,
    })
    .configureLogging(LogLevel.Warning)
    .build()
}

/**
 * The real source: one hub connection to the proxy, reconnecting with backoff.
 *
 * Reconnection is this transport's own rather than SignalR's withAutomaticReconnect,
 * because that only covers a connection that was established once — it does nothing for a
 * proxy that is not up yet, which is the usual case when someone opens the dashboard
 * first. One loop handles both.
 *
 * Frames that do not validate against the protocol are dropped and counted; the count
 * travels with the link status so the header can show it.
 */
export function createSignalRTransport(
  url: string,
  options: SignalRTransportOptions = {},
): Transport {
  const connect = options.connectionFactory ?? buildConnection
  const random = options.random ?? Math.random
  const onRejected = options.onRejected ?? warnOnce
  const getToken = options.getToken
  const onConnectFailed = options.onConnectFailed

  // SignalR wants a factory that always produces a string. There being no token is a real
  // state — the demo feed never has one — and an empty string is what the server then sees
  // as "no credentials", which is the honest answer.
  const accessTokenFactory = getToken ? () => getToken() ?? '' : undefined

  const events = createEmitter<ServerEvent>()
  const status = createEmitter<LinkStatus>()

  let connection: HubLike | null = null
  let attempt = 0
  let dropped = 0
  let retryTimer: number | undefined
  let stopped = true
  let last: LinkStatus = { state: 'idle', endpoint: url }

  const setStatus = (next: Omit<LinkStatus, 'endpoint' | 'dropped'>) => {
    last = { ...next, endpoint: url, dropped }
    status.emit(last)
  }

  const clearRetry = () => {
    if (retryTimer !== undefined) window.clearTimeout(retryTimer)
    retryTimer = undefined
  }

  const open = () => {
    if (stopped || connection) return
    setStatus({ state: attempt === 0 ? 'connecting' : 'retrying', attempt })

    let next: HubLike
    try {
      next = connect(url, accessTokenFactory)
    } catch (error) {
      scheduleRetry(messageOf(error, 'could not build a connection'))
      return
    }
    connection = next

    next.on('event', (payload: never) => {
      const result = parseServerEvent(payload)
      if (result.ok) {
        events.emit(result.event)
        return
      }
      dropped += 1
      onRejected(`${result.reason}: ${result.detail}`)
      // Re-emit so the badge count moves; the state itself is unchanged.
      setStatus({ state: last.state, attempt: last.attempt, detail: last.detail })
    })

    // Only fires for a connection that reached the server; a failed start is handled below.
    next.onclose((error) => {
      if (connection !== next) return
      connection = null
      if (stopped) return
      scheduleRetry(messageOf(error, 'the hub closed the connection'))
    })

    void next.start().then(
      () => {
        if (stopped || connection !== next) return
        attempt = 0
        setStatus({ state: 'live' })
      },
      (error: unknown) => {
        if (connection !== next) return
        connection = null
        if (stopped) return
        scheduleRetry(messageOf(error, 'could not reach the hub'))
      },
    )
  }

  const scheduleRetry = (detail: string) => {
    clearRetry()
    const delay = backoffFor(attempt, random)
    attempt += 1
    setStatus({ state: 'retrying', attempt, detail })
    onConnectFailed?.(detail)
    retryTimer = window.setTimeout(open, delay)
  }

  return {
    start: () => {
      if (!stopped) return
      stopped = false
      attempt = 0
      open()
    },
    stop: () => {
      stopped = true
      clearRetry()
      const current = connection
      connection = null
      void current?.stop().catch(() => {
        // Tearing down a connection that is already gone is not news.
      })
      setStatus({ state: 'closed' })
    },
    onEvent: (handler) => events.subscribe(handler),
    onStatus: (handler) => {
      handler(last)
      return status.subscribe(handler)
    },
  }
}

function messageOf(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) return error.message
  return typeof error === 'string' && error ? error : fallback
}

const warned = new Set<string>()
function warnOnce(detail: string) {
  const key = detail.split(':')[0] ?? detail
  if (warned.has(key)) return
  warned.add(key)
  console.warn(`[sitm] dropping frames (${detail}); further ones of this kind are silent`)
}
