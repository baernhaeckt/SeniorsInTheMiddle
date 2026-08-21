import { parseServerEvent, type ServerEvent } from '../protocol/types'
import { createEmitter, type LinkStatus, type Transport } from './types'

const BACKOFF_MS = [500, 1000, 2000, 4000, 8000, 15000]

/**
 * The real source: one WebSocket to the proxy, reconnecting with backoff.
 * The transport drops any frame that does not parse as a protocol event.
 */
export function createWebSocketTransport(url: string): Transport {
  const events = createEmitter<ServerEvent>()
  const status = createEmitter<LinkStatus>()

  let socket: WebSocket | null = null
  let attempt = 0
  let retryTimer: number | undefined
  let stopped = false
  let last: LinkStatus = { state: 'idle', endpoint: url }

  const setStatus = (next: LinkStatus) => {
    last = next
    status.emit(next)
  }

  const open = () => {
    if (stopped) return
    setStatus({ state: attempt === 0 ? 'connecting' : 'retrying', endpoint: url, attempt })

    try {
      socket = new WebSocket(url)
    } catch (error) {
      scheduleRetry(error instanceof Error ? error.message : 'could not open socket')
      return
    }

    socket.addEventListener('open', () => {
      attempt = 0
      setStatus({ state: 'live', endpoint: url })
    })

    socket.addEventListener('message', (message) => {
      const event = parseServerEvent(message.data)
      if (event) events.emit(event)
    })

    socket.addEventListener('error', () => {
      // `close` always follows; retry is scheduled there so it happens once.
    })

    socket.addEventListener('close', (event) => {
      socket = null
      if (stopped) return
      scheduleRetry(event.reason || `closed with code ${event.code}`)
    })
  }

  const scheduleRetry = (detail: string) => {
    const delay = BACKOFF_MS[Math.min(attempt, BACKOFF_MS.length - 1)]
    attempt += 1
    setStatus({ state: 'retrying', endpoint: url, attempt, detail })
    retryTimer = window.setTimeout(open, delay)
  }

  return {
    kind: 'websocket',
    start() {
      stopped = false
      if (last.state === 'idle') open()
    },
    stop() {
      stopped = true
      window.clearTimeout(retryTimer)
      socket?.close()
      socket = null
      setStatus({ state: 'closed', endpoint: url })
    },
    onEvent: events.subscribe,
    onStatus(handler) {
      handler(last)
      return status.subscribe(handler)
    },
  }
}
