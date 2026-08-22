import { parseServerEvent, type ServerEvent } from '../protocol/types'
import { createEmitter, type LinkStatus, type Transport } from './types'

export const BACKOFF_MS = [500, 1000, 2000, 4000, 8000, 15000] as const
/** Spread reconnects out, so several wall displays do not hammer the proxy in step. */
const JITTER = 0.2

export interface WebSocketTransportOptions {
  /** Injectable for tests. Defaults to the browser's WebSocket. */
  WebSocketImpl?: typeof WebSocket
  random?: () => number
  /** Called once per rejected frame, with the reason. Defaults to console.warn, throttled per reason. */
  onRejected?: (detail: string) => void
}

export function backoffFor(attempt: number, random = Math.random): number {
  const base = BACKOFF_MS[Math.min(attempt, BACKOFF_MS.length - 1)] ?? 0
  return Math.round(base * (1 + (random() * 2 - 1) * JITTER))
}

/**
 * The real source: one WebSocket to the proxy, reconnecting with backoff.
 * Frames that do not validate against the protocol are dropped and counted;
 * the count travels with the link status so the header can show it.
 */
export function createWebSocketTransport(
  url: string,
  options: WebSocketTransportOptions = {},
): Transport {
  const WS = options.WebSocketImpl ?? WebSocket
  const random = options.random ?? Math.random
  const onRejected = options.onRejected ?? warnOnce

  const events = createEmitter<ServerEvent>()
  const status = createEmitter<LinkStatus>()

  let socket: WebSocket | null = null
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
    if (stopped || socket) return
    setStatus({ state: attempt === 0 ? 'connecting' : 'retrying', attempt })

    let next: WebSocket
    try {
      next = new WS(url)
    } catch (error) {
      scheduleRetry(error instanceof Error ? error.message : 'could not open socket')
      return
    }
    socket = next

    next.addEventListener('open', () => {
      attempt = 0
      setStatus({ state: 'live' })
    })

    next.addEventListener('message', (message: MessageEvent<unknown>) => {
      const result = parseServerEvent(message.data)
      if (result.ok) {
        events.emit(result.event)
        return
      }
      dropped += 1
      onRejected(`${result.reason}: ${result.detail}`)
      // Re-emit so the badge count moves; the state itself is unchanged.
      setStatus({ state: last.state, attempt: last.attempt, detail: last.detail })
    })

    // `close` always follows `error`; retry is scheduled there so it happens once.
    next.addEventListener('close', (event) => {
      if (socket === next) socket = null
      if (stopped) return
      scheduleRetry(event.reason || `closed with code ${event.code}`)
    })
  }

  const scheduleRetry = (detail: string) => {
    clearRetry()
    const delay = backoffFor(attempt, random)
    attempt += 1
    setStatus({ state: 'retrying', attempt, detail })
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
      const current = socket
      socket = null
      current?.close()
      setStatus({ state: 'closed' })
    },
    onEvent: (handler) => events.subscribe(handler),
    onStatus: (handler) => {
      handler(last)
      return status.subscribe(handler)
    },
  }
}

const warned = new Set<string>()
function warnOnce(detail: string) {
  const key = detail.split(':')[0] ?? detail
  if (warned.has(key)) return
  warned.add(key)
  console.warn(`[sitm] dropping frames (${detail}); further ones of this kind are silent`)
}
